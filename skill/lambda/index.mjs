const relayUrl = (process.env.RELAY_URL ?? "").replace(/\/+$/, "");
const apiKey = process.env.RELAY_API_KEY ?? "";
const deviceId = process.env.DEVICE_ID ?? "pc-principal";

export const handler = async (event) => {
  const request = event?.request ?? {};
  logRequest(request);

  if (request.type === "LaunchRequest") {
    return ask("Bardo Control listo. ¿Qué quieres que haga?", "Puedes decir, por ejemplo, abre YouTube.");
  }

  if (request.type === "IntentRequest") {
    const intentName = request.intent?.name;

    if (intentName === "ExecuteCommandIntent") {
      const rawCommand = getCommandSlotValue(request);
      if (!rawCommand) {
        return ask("No he entendido el comando. ¿Qué quieres que haga?", "Di, por ejemplo, abre YouTube.");
      }

      const command = normalizeCommand(rawCommand);
      const result = await executeRemoteCommand(command);
      return speak(result.success ? "Hecho." : result.message);
    }

    if (intentName === "AMAZON.HelpIntent") {
      return ask(
        "Puedes pedirme que ejecute cualquiera de los comandos configurados en AlexaPc. Por ejemplo, abre YouTube o pausa.",
        "¿Qué quieres que haga?"
      );
    }

    if (intentName === "AMAZON.StopIntent" || intentName === "AMAZON.CancelIntent") {
      return speak("Vale.");
    }

    if (intentName === "AMAZON.FallbackIntent") {
      return ask("No he reconocido esa orden de Bardo Control.", "Prueba con abre YouTube.");
    }
  }

  if (request.type === "SessionEndedRequest") {
    return { version: "1.0", response: {} };
  }

  return ask("¿Qué quieres que haga en el PC?", "Puedes decir abre YouTube.");
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
    "reinicia el pc": "reinicia ordenador"
  };

  return aliases[normalized] ?? normalized;
}

async function executeRemoteCommand(command) {
  if (!relayUrl || !apiKey) {
    return {
      success: false,
      message: "La conexión de Bardo todavía no está configurada."
    };
  }

  try {
    const response = await fetch(`${relayUrl}/api/commands`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-alexapc-api-key": apiKey
      },
      body: JSON.stringify({ deviceId, command })
    });

    const payload = await response.json().catch(() => ({}));

    if (!response.ok) {
      return {
        success: false,
        message: payload.message ?? "No he podido comunicarme con el PC."
      };
    }

    return {
      success: payload.success === true,
      message: payload.message ?? "No se pudo ejecutar la orden."
    };
  } catch {
    return {
      success: false,
      message: "No he podido conectar con el servicio del PC."
    };
  }
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
