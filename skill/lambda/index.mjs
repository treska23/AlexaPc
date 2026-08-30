const relayUrl = (process.env.RELAY_URL ?? "").replace(/\/+$/, "");
const apiKey = process.env.RELAY_API_KEY ?? "";
const deviceId = process.env.DEVICE_ID ?? "pc-principal";
export const requestTimeoutMs = 7200;
const assistantPrefix = "[bardo]";

export const handler = async (event) => {
  const request = event?.request ?? {};
  logRequest(request);

  if (request.type === "LaunchRequest") {
    return ask(
      "Bardo Control listo. ¿Qué quieres que haga?",
      "Puedes darme una orden o pedirme algo con tus propias palabras."
    );
  }

  if (request.type === "IntentRequest") {
    const intentName = request.intent?.name;

    const fixedCommand = commandForIntent(intentName);
    if (fixedCommand) {
      return executeAndRespond(fixedCommand);
    }

    if (intentName === "ExecuteCommandIntent") {
      const rawCommand = getCommandSlotValue(request);
      if (!rawCommand) {
        return ask("No he entendido la petición. ¿Qué quieres que haga?", "Dímelo con tus propias palabras.");
      }

      const command = normalizeCommand(rawCommand);
      return executeAndRespond(command);
    }

    if (intentName === "AMAZON.HelpIntent") {
      return ask(
        "Puedes darme órdenes del ordenador o hablarme de forma natural. Si no reconozco un comando directo, lo intentará entender el asistente local.",
        "¿Qué quieres que haga?"
      );
    }

    if (intentName === "AMAZON.StopIntent" || intentName === "AMAZON.CancelIntent") {
      return speak("Vale.");
    }

    if (intentName === "AMAZON.FallbackIntent") {
      return ask("No he reconocido esa petición de Bardo Control.", "Prueba a decir quiero, necesito, dime o abre, seguido de lo que quieras.");
    }
  }

  if (request.type === "SessionEndedRequest") {
    return { version: "1.0", response: {} };
  }

  return ask("¿Qué quieres que haga en el ordenador?", "Puedes darme una orden o pedirme algo con tus propias palabras.");
};

function getCommandSlotValue(request) {
  return request.intent?.slots?.command?.value?.trim() || null;
}

function logRequest(request) {
  const isIntentRequest = request.type === "IntentRequest";
  const entry = {
    component: "AlexaPc.Skill",
    event: "request",
    requestType: request.type ?? "UnknownRequest",
    requestId: request.requestId ?? null,
    locale: request.locale ?? null,
    intentName: isIntentRequest ? request.intent?.name ?? null : null,
    command: isIntentRequest ? getCommandSlotValue(request) : null
  };

  console.info(JSON.stringify(entry));
}

export function normalizeCommand(value) {
  const normalized = value.toLocaleLowerCase("es-ES").trim().replace(/\s+/g, " ");
  const aliases = {
    "you tube": "youtube",
    "el youtube": "youtube",
    "bloc notas": "bloc de notas",
    "el bloc de notas": "bloc de notas",
    "sube el volumen": "sube volumen",
    "baja el volumen": "baja volumen",
    "bloquea el ordenador": "bloquea ordenador",
    "bloquea el pc": "bloquea ordenador",
    "suspende el ordenador": "suspende ordenador",
    "suspende el pc": "suspende ordenador",
    "apaga el ordenador": "apaga ordenador",
    "apaga el pc": "apaga ordenador",
    "reinicia el ordenador": "reinicia ordenador",
    "reinicia el pc": "reinicia ordenador",
    "reanuda": "reproduce",
    "reanude": "reproduce",
    "reanuda la reproducción": "reproduce",
    "reanuda la reproduccion": "reproduce",
    "reanudar la reproducción": "reproduce",
    "reanudar la reproduccion": "reproduce",
    "continúa": "reproduce",
    "continua": "reproduce",
    "continúe": "reproduce",
    "continue": "reproduce",
    "continúa la reproducción": "reproduce",
    "continua la reproduccion": "reproduce",
    "reproducir": "reproduce",
    "empieza a reproducir": "reproduce",
    "empiece a reproducir": "reproduce",
    "inicia la reproducción": "reproduce",
    "inicia la reproduccion": "reproduce",
    "pausa la reproducción": "pausa",
    "pausa la reproduccion": "pausa",
    "pausar": "pausa",
    "pon pausa": "pausa"
  };

  return aliases[normalized] ?? normalized;
}

export function commandForIntent(intentName) {
  const commands = {
    "AMAZON.PauseIntent": "pausa",
    "AMAZON.ResumeIntent": "reproduce",
    MediaPauseIntent: "pausa",
    MediaPlayIntent: "reproduce",
    ShutdownComputerIntent: "apaga ordenador",
    RestartComputerIntent: "reinicia ordenador",
    SleepComputerIntent: "suspende ordenador",
    LockComputerIntent: "bloquea ordenador"
  };

  return commands[intentName] ?? null;
}

async function executeAndRespond(command) {
  const result = await executeRemoteCommand(command);
  if (!result.success) {
    return speak(result.message);
  }

  const assistantMessage = extractAssistantMessage(result.message);
  if (assistantMessage) {
    return speak(assistantMessage);
  }

  return speak(command.endsWith("ordenador") ? result.message : "Hecho.");
}

export function extractAssistantMessage(message) {
  const text = String(message ?? "").trim();
  if (!text.toLocaleLowerCase("es-ES").startsWith(assistantPrefix)) {
    return null;
  }

  return text.slice(assistantPrefix.length).trim() || "Hecho.";
}

async function executeRemoteCommand(command) {
  if (!relayUrl || !apiKey) {
    return {
      success: false,
      message: "La conexión de Bardo todavía no está configurada."
    };
  }

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), requestTimeoutMs);
  const startedAt = Date.now();

  try {
    const response = await fetch(`${relayUrl}/api/commands`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-alexapc-api-key": apiKey
      },
      body: JSON.stringify({ deviceId, command }),
      signal: controller.signal
    });

    const payload = await response.json().catch(() => ({}));
    logRelayResult(command, response.status, response.ok && payload.success === true, Date.now() - startedAt);

    if (!response.ok) {
      return {
        success: false,
        message: payload.message ?? "No he podido comunicarme con el ordenador."
      };
    }

    return {
      success: payload.success === true,
      message: payload.message ?? "No se pudo ejecutar la orden."
    };
  } catch (error) {
    logRelayResult(
      command,
      null,
      false,
      Date.now() - startedAt,
      error?.name === "AbortError" ? "timeout" : "network_error"
    );
    return {
      success: false,
      message: "No he podido conectar con el servicio del ordenador."
    };
  } finally {
    clearTimeout(timeout);
  }
}

function logRelayResult(command, status, success, durationMs, error = null) {
  console.info(JSON.stringify({
    component: "AlexaPc.Skill",
    event: "relay_result",
    command,
    status,
    success,
    durationMs,
    error
  }));
}

function speak(text) {
  return {
    version: "1.0",
    response: {
      outputSpeech: { type: "PlainText", text },
      shouldEndSession: true
    }
  };
}

function ask(text, reprompt) {
  return {
    version: "1.0",
    response: {
      outputSpeech: { type: "PlainText", text },
      reprompt: {
        outputSpeech: { type: "PlainText", text: reprompt }
      },
      shouldEndSession: false
    }
  };
}
