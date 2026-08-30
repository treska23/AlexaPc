import { DurableObject } from "cloudflare:workers";

const COMMAND_TIMEOUT_MS = 6200;
const MAX_COMMAND_LENGTH = 120;

export default {
  async fetch(request, env) {
    const requestId = crypto.randomUUID();
    const startedAt = Date.now();

    try {
      const response = await routeRequest(request, env, requestId);
      log("info", "request_completed", {
        requestId,
        method: request.method,
        path: new URL(request.url).pathname,
        status: response.status,
        durationMs: Date.now() - startedAt
      });
      return response;
    } catch (error) {
      log("error", "request_failed", {
        requestId,
        method: request.method,
        path: new URL(request.url).pathname,
        durationMs: Date.now() - startedAt,
        error: error instanceof Error ? error.message : String(error)
      });
      return json({ success: false, message: "Error interno del relay." }, 500);
    }
  }
};

async function routeRequest(request, env, edgeRequestId) {
  const url = new URL(request.url);

  if (url.pathname === "/health" && request.method === "GET") {
    const deviceId = url.searchParams.get("deviceId")?.trim();
    if (!deviceId) {
      return json({ status: "ok", service: "AlexaPc Cloud Relay" });
    }

    const stub = env.RELAY.getByName(deviceId);
    return stub.fetch("https://relay.internal/health");
  }

  if (url.pathname === "/ws/agent") {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return json({ message: "WebSocket requerido." }, 426);
    }

    const deviceId = url.searchParams.get("deviceId")?.trim() ?? "";
    const token = url.searchParams.get("token") ?? "";

    if (!deviceId || !await secureEquals(token, env.DEVICE_TOKEN ?? "")) {
      return json({ message: "No autorizado." }, 401);
    }

    const stub = env.RELAY.getByName(deviceId);
    return stub.fetch(new Request("https://relay.internal/ws", {
      method: "GET",
      headers: request.headers
    }));
  }

  if (url.pathname === "/api/commands" && request.method === "POST") {
    const suppliedKey = request.headers.get("x-alexapc-api-key") ?? "";
    if (!await secureEquals(suppliedKey, env.RELAY_API_KEY ?? "")) {
      return json({ success: false, message: "No autorizado." }, 401);
    }

    let payload;
    try {
      payload = await request.json();
    } catch {
      return json({ success: false, message: "JSON no válido." }, 400);
    }

    const deviceId = String(payload?.deviceId ?? "").trim();
    const command = String(payload?.command ?? "").trim();
    if (!deviceId || !command) {
      return json({ success: false, message: "deviceId y command son obligatorios." }, 400);
    }

    if (command.length > MAX_COMMAND_LENGTH) {
      return json({ success: false, message: "El comando es demasiado largo." }, 400);
    }

    const stub = env.RELAY.getByName(deviceId);
    log("info", "command_api_accepted", {
      edgeRequestId,
      command
    });
    return stub.fetch("https://relay.internal/command", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ command })
    });
  }

  if (url.pathname === "/" && request.method === "GET") {
    return json({ message: "AlexaPc Cloud Relay" });
  }

  return json({ message: "Not found" }, 404);
}

export class AlexaPcRelay extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.pending = new Map();
    this.commandTimeoutMs = resolveCommandTimeout(env.COMMAND_TIMEOUT_MS);
  }

  async fetch(request) {
    const url = new URL(request.url);

    if (url.pathname === "/health") {
      return json({
        status: "ok",
        connectedAgents: this.getConnectedAgents().length,
        pendingCommands: this.pending.size,
        commandTimeoutMs: this.commandTimeoutMs
      });
    }

    if (url.pathname === "/ws") {
      if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
        return json({ message: "WebSocket requerido." }, 426);
      }

      const pair = new WebSocketPair();
      const [client, server] = Object.values(pair);
      this.ctx.acceptWebSocket(server, ["agent"]);
      server.serializeAttachment({
        connectionId: crypto.randomUUID(),
        connectedAt: Date.now()
      });

      // More than one copy of the desktop agent can legitimately overlap for a
      // few seconds (startup, an update, another Windows session, etc.). Closing
      // the previous socket here makes both clients reconnect and replace each
      // other forever. Keep every healthy socket and route commands to the most
      // recently connected one instead.
      log("info", "agent_connected", {
        connectedAgents: this.getConnectedAgents().length
      });
      return new Response(null, { status: 101, webSocket: client });
    }

    if (url.pathname === "/command" && request.method === "POST") {
      const { command } = await request.json();
      const socket = this.getPrimaryAgent();

      if (!socket) {
        return json({ success: false, message: "El ordenador no está conectado al relay." }, 503);
      }

      const requestId = crypto.randomUUID();
      const startedAt = Date.now();
      log("info", "command_received", {
        requestId,
        command,
        connectedAgents: this.getConnectedAgents().length,
        pendingCommands: this.pending.size
      });

      const responsePromise = new Promise(resolve => {
        const timeout = setTimeout(() => {
          log("error", "command_timed_out", {
            requestId,
            command,
            durationMs: Date.now() - startedAt
          });
          this.completePending(requestId, {
            success: false,
            message: "El ordenador no respondió a tiempo.",
            status: 504
          });
        }, this.commandTimeoutMs);

        this.pending.set(requestId, { socket, timeout, resolve });
      });

      try {
        socket.send(JSON.stringify({ type: "execute", requestId, command }));
        log("info", "command_dispatched", { requestId, command });
      } catch (error) {
        this.completePending(requestId, {
          success: false,
          message: "No se pudo enviar la orden al ordenador.",
          status: 503
        });
        log("error", "command_send_failed", {
          requestId,
          error: error instanceof Error ? error.message : String(error)
        });
      }

      const result = await responsePromise;
      const { status, ...payload } = result;
      log(payload.success ? "info" : "error", "command_completed", {
        requestId,
        command,
        success: payload.success,
        status,
        durationMs: Date.now() - startedAt
      });
      return json(payload, status);
    }

    return json({ message: "Not found" }, 404);
  }

  webSocketMessage(socket, message) {
    if (typeof message !== "string") {
      return;
    }

    let payload;
    try {
      payload = JSON.parse(message);
    } catch {
      return;
    }

    if (payload?.type !== "result" || !payload?.requestId) {
      return;
    }

    const pending = this.pending.get(payload.requestId);
    if (!pending) {
      log("error", "command_result_ignored", {
        requestId: payload.requestId,
        reason: "not_pending"
      });
      return;
    }

    if (pending.socket !== socket) {
      log("error", "command_result_ignored", {
        requestId: payload.requestId,
        reason: "wrong_socket"
      });
      return;
    }

    log("info", "command_result_received", {
      requestId: payload.requestId,
      success: payload.success === true
    });

    this.completePending(payload.requestId, {
      success: payload.success === true,
      message: payload.message ?? (payload.success ? "Hecho." : "No se pudo ejecutar la orden."),
      status: 200
    });
  }

  webSocketClose(socket, code, reason, wasClean) {
    this.failPendingForSocket(socket, "El ordenador se desconectó durante la orden.");
    log("info", "agent_disconnected", {
      code,
      reason,
      wasClean,
      connectedAgents: this.getConnectedAgents().length
    });
  }

  webSocketError(socket, error) {
    this.failPendingForSocket(socket, "Se perdió la conexión con el ordenador.");
    log("error", "agent_websocket_error", {
      error: error instanceof Error ? error.message : String(error)
    });
  }

  getConnectedAgents() {
    return this.ctx
      .getWebSockets("agent")
      .filter(socket => socket.readyState === WebSocket.OPEN);
  }

  getPrimaryAgent() {
    return this.getConnectedAgents()
      .sort((left, right) => connectionTime(right) - connectionTime(left))[0] ?? null;
  }

  completePending(requestId, result) {
    const pending = this.pending.get(requestId);
    if (!pending) {
      return;
    }

    this.pending.delete(requestId);
    clearTimeout(pending.timeout);
    pending.resolve(result);
  }

  failPendingForSocket(socket, message) {
    for (const [requestId, pending] of this.pending) {
      if (pending.socket === socket) {
        this.completePending(requestId, { success: false, message, status: 503 });
      }
    }
  }
}

function connectionTime(socket) {
  try {
    return Number(socket.deserializeAttachment()?.connectedAt ?? 0);
  } catch {
    return 0;
  }
}

function resolveCommandTimeout(value) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) {
    return COMMAND_TIMEOUT_MS;
  }

  return Math.min(COMMAND_TIMEOUT_MS, Math.max(50, Math.round(parsed)));
}

function json(payload, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" }
  });
}

async function secureEquals(left, right) {
  const encoder = new TextEncoder();
  const [leftHash, rightHash] = await Promise.all([
    crypto.subtle.digest("SHA-256", encoder.encode(left)),
    crypto.subtle.digest("SHA-256", encoder.encode(right))
  ]);
  return crypto.subtle.timingSafeEqual(leftHash, rightHash) && Boolean(left) && Boolean(right);
}

function log(level, eventName, details = {}) {
  const entry = JSON.stringify({
    timestamp: new Date().toISOString(),
    level,
    eventName,
    ...details
  });

  if (level === "error") {
    console.error(entry);
  } else {
    console.log(entry);
  }
}
