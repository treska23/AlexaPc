import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { unstable_dev } from "wrangler";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const apiKey = "integration-api-key";
const deviceId = "integration-device";
const deviceToken = "integration-device-token";

async function createRelay(commandTimeoutMs = 120) {
  return unstable_dev(path.join(workerRoot, "src", "index.js"), {
    config: path.join(workerRoot, "wrangler.jsonc"),
    compatibilityDate: "2026-08-30",
    compatibilityFlags: ["nodejs_compat"],
    vars: {
      RELAY_API_KEY: apiKey,
      DEVICE_TOKEN: deviceToken,
      COMMAND_TIMEOUT_MS: String(commandTimeoutMs)
    },
    persist: false,
    logLevel: "none",
    experimental: {
      disableExperimentalWarning: true,
      disableDevRegistry: true,
      watch: false
    }
  });
}

async function getHealth(relay) {
  const response = await relay.fetch(
    `http://relay.test/health?deviceId=${encodeURIComponent(deviceId)}`
  );
  assert.equal(response.status, 200);
  return response.json();
}

async function sendCommand(relay, command, key = apiKey) {
  const response = await relay.fetch("http://relay.test/api/commands", {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-alexapc-api-key": key
    },
    body: JSON.stringify({ deviceId, command })
  });

  return { status: response.status, body: await response.json() };
}

async function connectAgent(relay, onExecute) {
  const address = relay.address.includes(":") ? `[${relay.address}]` : relay.address;
  const socket = new WebSocket(
    `ws://${address}:${relay.port}/ws/agent?deviceId=${encodeURIComponent(deviceId)}&token=${encodeURIComponent(deviceToken)}`
  );
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  socket.addEventListener("message", event => {
    const message = JSON.parse(event.data);
    onExecute(message, result => socket.send(JSON.stringify({
      type: "result",
      requestId: message.requestId,
      ...result
    })));
  });
  return socket;
}

test("health and authentication report the real relay state", async t => {
  const relay = await createRelay();
  t.after(() => relay.stop());

  assert.deepEqual(await getHealth(relay), {
    status: "ok",
    connectedAgents: 0,
    pendingCommands: 0,
    commandTimeoutMs: 120
  });

  const unauthorized = await sendCommand(relay, "pausa", "wrong-key");
  assert.equal(unauthorized.status, 401);
  assert.equal(unauthorized.body.success, false);
});

test("an agent can answer exact, natural and concurrent commands", async t => {
  const relay = await createRelay(300);
  t.after(() => relay.stop());

  const socket = await connectAgent(relay, (message, reply) => {
    const delay = message.command === "qué es un agujero negro" ? 35 : 10;
    setTimeout(() => reply({
      success: true,
      message: message.command === "pausa" ? "Pausado." : `[bardo] ${message.command}`
    }), delay);
  });
  t.after(() => socket.close(1000, "test complete"));

  assert.equal((await getHealth(relay)).connectedAgents, 1);

  const exact = await sendCommand(relay, "pausa");
  assert.deepEqual(exact, {
    status: 200,
    body: { success: true, message: "Pausado." }
  });

  const [natural, second] = await Promise.all([
    sendCommand(relay, "qué es un agujero negro"),
    sendCommand(relay, "quiero ver youtube")
  ]);

  assert.equal(natural.status, 200);
  assert.equal(natural.body.success, true);
  assert.match(natural.body.message, /agujero negro/);
  assert.equal(second.status, 200);
  assert.equal(second.body.success, true);
  assert.equal((await getHealth(relay)).pendingCommands, 0);
});

test("a missing result times out and always clears the pending request", async t => {
  const relay = await createRelay(80);
  t.after(() => relay.stop());

  const socket = await connectAgent(relay, () => {
    // Simula un agente que acepta la orden pero nunca devuelve resultado.
  });
  t.after(() => socket.close(1000, "test complete"));

  const result = await sendCommand(relay, "sin respuesta");
  assert.equal(result.status, 504);
  assert.deepEqual(result.body, {
    success: false,
    message: "El ordenador no respondió a tiempo."
  });
  assert.equal((await getHealth(relay)).pendingCommands, 0);
});
