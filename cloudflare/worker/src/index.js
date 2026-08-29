import { DurableObject } from "cloudflare:workers";

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === "/health" && request.method === "GET") {
      return json({ status: "ok", service: "AlexaPc Cloud Relay" });
    }

    if (url.pathname === "/ws/agent") {
      if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
        return json({ message: "WebSocket requerido." }, 400);
      }

      const deviceId = url.searchParams.get("deviceId")?.trim() ?? "";
      const token = url.searchParams.get("token") ?? "";

      if (!deviceId || !secureEquals(token, env.DEVICE_TOKEN ?? "")) {
        return json({ message: "No autorizado." }, 401);
      }

      const stub = env.RELAY.getByName(deviceId);
      return stub.fetch(new Request("https://relay.internal/ws", request));
    }

    if (url.pathname === "/api/commands" && request.method === "POST") {
      const suppliedKey = request.headers.get("x-alexapc-api-key") ?? "";
      if (!secureEquals(suppliedKey, env.RELAY_API_KEY ?? "")) {
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

      const stub = env.RELAY.getByName(deviceId);
      return stub.fetch("https://relay.internal/command", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ command })
      });
    }

    return json({ message: "AlexaPc Cloud Relay" }, 200);
  }
};

export class AlexaPcRelay extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.pending = new Map();
  }

  async fetch(request) {
    const url = new URL(request.url);

    if (url.pathname === "/ws") {
      if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
        return json({ message: "WebSocket requerido." }, 400);
      }

      const pair = new WebSocketPair();
      const client = pair[0];
      const server = pair[1];
      this.ctx.acceptWebSocket(server, ["agent"]);

      return new Response(null, { status: 101, webSocket: client });
    }

    if (url.pathname === "/command" && request.method === "POST") {
      const { command } = await request.json();
      const sockets = this.ctx.getWebSockets("agent").filter(ws => ws.readyState === WebSocket.OPEN);

      if (sockets.length === 0) {
        return json({ success: false, message: "El PC no está conectado al relay." }, 503);
      }

      const requestId = crypto.randomUUID();
      const responsePromise = new Promise(resolve => {
        const timeout = setTimeout(() => {
          this.pending.delete(requestId);
          resolve({ success: false, message: "El PC no respondió a tiempo." });
        }, 10000);

        this.pending.set(requestId, value => {
          clearTimeout(timeout);
          resolve(value);
        });
      });

      try {
        sockets[0].send(JSON.stringify({ type: "execute", requestId, command }));
      } catch {
        this.pending.delete(requestId);
        return json({ success: false, message: "No se pudo enviar la orden al PC." }, 503);
      }

      const result = await responsePromise;
      return json(result, result.success ? 200 : 503);
    }

    return json({ message: "Not found" }, 404);
  }

  webSocketMessage(_socket, message) {
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

    const complete = this.pending.get(payload.requestId);
    if (!complete) {
      return;
    }

    this.pending.delete(payload.requestId);
    complete({
      success: payload.success === true,
      message: payload.message ?? (payload.success ? "Hecho." : "No se pudo ejecutar la orden.")
    });
  }

  webSocketClose(socket, code, reason) {
    try {
      socket.close(code, reason);
    } catch {
    }
  }

  webSocketError() {
  }
}

function json(payload, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { "content-type": "application/json; charset=utf-8" }
  });
}

function secureEquals(left, right) {
  if (!left || !right || left.length !== right.length) {
    return false;
  }

  let diff = 0;
  for (let i = 0; i < left.length; i++) {
    diff |= left.charCodeAt(i) ^ right.charCodeAt(i);
  }
  return diff === 0;
}
