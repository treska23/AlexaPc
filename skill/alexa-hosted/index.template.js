const https = require('https');

const RELAY_URL = __RELAY_URL_JSON__;
const RELAY_API_KEY = __RELAY_API_KEY_JSON__;
const DEVICE_ID = __DEVICE_ID_JSON__;

exports.handler = async (event) => {
  const request = event?.request ?? {};
  logRequest(request);

  if (request.type === 'LaunchRequest') {
    return ask('Bardo Control listo. ¿Qué quieres que haga?', 'Puedes decir, por ejemplo, abre YouTube.');
  }

  if (request.type === 'IntentRequest') {
    const intentName = request.intent?.name;

    if (intentName === 'ExecuteCommandIntent') {
      const rawCommand = getCommandSlotValue(request);
      if (!rawCommand) {
        return ask('No he entendido el comando. ¿Qué quieres que haga?', 'Di, por ejemplo, abre YouTube.');
      }

      const command = normalizeCommand(rawCommand);
      const result = await executeRemoteCommand(command);
      return speak(result.success ? 'Hecho.' : result.message);
    }

    if (intentName === 'AMAZON.HelpIntent') {
      return ask(
        'Puedes pedirme que ejecute cualquiera de los comandos configurados en AlexaPc. Por ejemplo, abre YouTube.',
        '¿Qué quieres que haga?'
      );
    }

    if (intentName === 'AMAZON.StopIntent' || intentName === 'AMAZON.CancelIntent') {
      return speak('Vale.');
    }

    if (intentName === 'AMAZON.FallbackIntent') {
      return ask('No he reconocido esa orden de Bardo Control.', 'Prueba con abre YouTube.');
    }
  }

  if (request.type === 'SessionEndedRequest') {
    return { version: '1.0', response: {} };
  }

  return ask('¿Qué quieres que haga en el PC?', 'Puedes decir abre YouTube.');
};

function getCommandSlotValue(request) {
  return request.intent?.slots?.command?.value?.trim() || null;
}

function logRequest(request) {
  const isIntentRequest = request.type === 'IntentRequest';
  const entry = {
    component: 'AlexaPc.Skill',
    event: 'request',
    requestType: request.type ?? 'UnknownRequest',
    requestId: request.requestId ?? null,
    locale: request.locale ?? null,
    intentName: isIntentRequest ? request.intent?.name ?? null : null,
    command: isIntentRequest ? getCommandSlotValue(request) : null
  };

  console.info(JSON.stringify(entry));
}

function normalizeCommand(value) {
  const normalized = value.toLocaleLowerCase('es-ES').trim().replace(/\s+/g, ' ');
  const aliases = {
    'you tube': 'youtube',
    'el youtube': 'youtube',
    'bloc notas': 'bloc de notas',
    'el bloc de notas': 'bloc de notas',
    'sube el volumen': 'sube volumen',
    'baja el volumen': 'baja volumen',
    'bloquea el ordenador': 'bloquea ordenador',
    'bloquea el pc': 'bloquea ordenador',
    'suspende el ordenador': 'suspende ordenador',
    'suspende el pc': 'suspende ordenador',
    'apaga el ordenador': 'apaga ordenador',
    'apaga el pc': 'apaga ordenador',
    'reinicia el ordenador': 'reinicia ordenador',
    'reinicia el pc': 'reinicia ordenador'
  };

  return aliases[normalized] ?? normalized;
}

function executeRemoteCommand(command) {
  return new Promise((resolve) => {
    let endpoint;
    try {
      endpoint = new URL('/api/commands', RELAY_URL);
    } catch {
      resolve({ success: false, message: 'La dirección del relay no es válida.' });
      return;
    }

    const body = JSON.stringify({ deviceId: DEVICE_ID, command });
    const req = https.request({
      protocol: endpoint.protocol,
      hostname: endpoint.hostname,
      port: endpoint.port || 443,
      path: endpoint.pathname + endpoint.search,
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'content-length': Buffer.byteLength(body),
        'x-alexapc-api-key': RELAY_API_KEY
      },
      timeout: 8000
    }, (res) => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', chunk => data += chunk);
      res.on('end', () => {
        let payload = {};
        try { payload = JSON.parse(data); } catch {}

        if (res.statusCode >= 200 && res.statusCode < 300) {
          resolve({
            success: payload.success === true,
            message: payload.message ?? 'No se pudo ejecutar la orden.'
          });
          return;
        }

        resolve({
          success: false,
          message: payload.message ?? 'No he podido comunicarme con el PC.'
        });
      });
    });

    req.on('timeout', () => {
      req.destroy(new Error('timeout'));
    });

    req.on('error', () => {
      resolve({ success: false, message: 'No he podido conectar con el servicio del PC.' });
    });

    req.write(body);
    req.end();
  });
}

function speak(text) {
  return {
    version: '1.0',
    response: {
      outputSpeech: { type: 'PlainText', text },
      shouldEndSession: true
    }
  };
}

function ask(text, reprompt) {
  return {
    version: '1.0',
    response: {
      outputSpeech: { type: 'PlainText', text },
      reprompt: { outputSpeech: { type: 'PlainText', text: reprompt } },
      shouldEndSession: false
    }
  };
}

exports._test = { normalizeCommand };
