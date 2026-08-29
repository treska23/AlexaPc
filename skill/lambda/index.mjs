const relayUrl = (process.env.RELAY_URL ?? "").replace(/\/+$/, "");
const apiKey = process.env.RELAY_API_KEY ?? "";
const deviceId = process.env.DEVICE_ID ?? "pc-principal";

export const handler = async (event) => {
  const request = event?.request ?? {};

  if (request.type === "LaunchRequest") {
    return ask("Control PC listo. ¿Qué quieres que haga?", "Puedes decir, por ejemplo, abre YouTube.");
  }

  if (request.type === "IntentRequest") {
    const intentName = request.intent?.name;

    if (intentName === "ExecuteCommandIntent") {
      const command = request.intent?.slots?.command?.value?.trim();
      if (!command) {
        return ask("No he entendido el comando. ¿Qué quieres que haga?", "Di, por ejemplo, abre YouTube.");
      }

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
      return ask("No he reconocido esa orden de AlexaPc.", "Prueba con abre YouTube.");
    }
  }

  if (request.type === "SessionEndedRequest") {
    return { version: "1.0", response: {} };
  }

  return ask("¿Qué quieres que haga en el PC?", "Puedes decir abre YouTube.");
};

async function executeRemoteCommand(command) {
  if (!relayUrl || !apiKey) {
    return {
      success: false,
      message: "La conexión de Control PC todavía no está configurada."
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
