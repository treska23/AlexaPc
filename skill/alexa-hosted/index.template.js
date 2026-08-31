const https = require('https');

const RELAY_URL = __RELAY_URL_JSON__;
const RELAY_API_KEY = __RELAY_API_KEY_JSON__;
const DEVICE_ID = __DEVICE_ID_JSON__;
const REQUEST_TIMEOUT_MS = 7200;
const ASSISTANT_PREFIX = '[bardo]';

exports.handler = async (event) => {
  const request = event?.request ?? {};
  logRequest(request);

  if (request.type === 'LaunchRequest') {
    return ask(
      'Bardo Control listo. ¿Qué quieres que haga?',
      'Puedes darme una orden o pedirme algo con tus propias palabras.'
    );
  }

  if (request.type === 'IntentRequest') {
    const intentName = request.intent?.name;

    const fixedCommand = commandForIntent(intentName);
    if (fixedCommand) {
      return executeAndRespond(fixedCommand);
    }

    const naturalAction = commandForActionIntent(request.intent);
    if (naturalAction) {
      return executeAndRespond(naturalAction);
    }

    const naturalQuestion = commandForQuestionIntent(request.intent);
    if (naturalQuestion) {
      return executeAndRespond(naturalQuestion);
    }

    if (intentName === 'ExecuteCommandIntent') {
      const rawCommand = getCommandSlotValue(request);
      if (!rawCommand) {
        return ask('No he entendido la petición. ¿Qué quieres que haga?', 'Dímelo con tus propias palabras.');
      }

      const command = normalizeCommand(rawCommand);
      return executeAndRespond(command);
    }

    if (intentName === 'AMAZON.HelpIntent') {
      return ask(
        'Puedes pedirme que abra aplicaciones, busque en Internet, controle ventanas, pantallas o reproducción y encadene varias acciones.',
        '¿Qué quieres que haga?'
      );
    }

    if (intentName === 'AMAZON.StopIntent' || intentName === 'AMAZON.CancelIntent') {
      return speak('Vale.');
    }

    if (intentName === 'AMAZON.FallbackIntent') {
      return ask('No he reconocido la orden para el ordenador.', 'Prueba a decir abre, cierra, busca, pon, cambia o dime, seguido de lo que quieras.');
    }
  }

  if (request.type === 'SessionEndedRequest') {
    return { version: '1.0', response: {} };
  }

  return ask('¿Qué quieres que haga en el ordenador?', 'Puedes darme una orden o pedirme algo con tus propias palabras.');
};

function getCommandSlotValue(request) {
  return request.intent?.slots?.command?.value?.trim() || null;
}

function commandForActionIntent(intent) {
  const definitions = {
    OpenComputerIntent: { slot: 'target', prefix: 'abre' },
    CloseComputerIntent: { slot: 'target', prefix: 'cierra' },
    SearchComputerIntent: { slot: 'query', prefix: 'busca' },
    SetComputerIntent: { slot: 'instruction', prefix: 'pon' },
    ChangeComputerIntent: { slot: 'instruction', prefix: 'cambia' },
    ActivateComputerIntent: { slot: 'target', prefix: 'activa' },
    DeactivateComputerIntent: { slot: 'target', prefix: 'desactiva' },
    MaximizeWindowIntent: { slot: 'target', prefix: 'maximiza' },
    MinimizeWindowIntent: { slot: 'target', prefix: 'minimiza' },
    RestoreWindowIntent: { slot: 'target', prefix: 'restaura' },
    BringWindowIntent: { slot: 'target', prefix: 'trae', suffix: 'al frente' },
    PlaceWindowIntent: { slot: 'instruction', prefix: 'coloca' },
    DuplicateDisplaysIntent: { slot: 'target', prefix: 'duplica' },
    ExtendDisplaysIntent: { slot: 'target', prefix: 'extiende' },
    RotateDisplayIntent: { slot: 'target', prefix: 'gira' },
    ForwardMediaIntent: { slot: 'target', prefix: 'adelanta' },
    RewindMediaIntent: { slot: 'target', prefix: 'retrocede' },
    WhatComputerIntent: { slot: 'query', prefix: 'qué' }
  };
  const definition = definitions[intent?.name];
  const value = definition ? intent?.slots?.[definition.slot]?.value?.trim() : null;
  if (!value) {
    return null;
  }

  return `${definition.prefix} ${value}${definition.suffix ? ` ${definition.suffix}` : ''}`;
}

function commandForQuestionIntent(intent) {
  const definitions = {
    WhatIsIntent: { slot: 'topic', prefix: 'qué es' },
    WhoIsIntent: { slot: 'person', prefix: 'quién es' },
    HowQuestionIntent: { slot: 'subject', prefix: 'cómo funciona' },
    WhyQuestionIntent: { slot: 'subject', prefix: 'por qué' },
    WhereQuestionIntent: { slot: 'subject', prefix: 'dónde está' },
    WhenQuestionIntent: { slot: 'subject', prefix: 'cuándo es' }
  };
  const definition = definitions[intent?.name];
  const value = definition ? intent?.slots?.[definition.slot]?.value?.trim() : null;
  return value ? `${definition.prefix} ${value}` : null;
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
    command: isIntentRequest
      ? getCommandSlotValue(request)
        ?? commandForActionIntent(request.intent)
        ?? commandForQuestionIntent(request.intent)
      : null
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
    'reinicia el pc': 'reinicia ordenador',
    'reanuda': 'reproduce',
    'reanude': 'reproduce',
    'reanuda la reproducción': 'reproduce',
    'reanuda la reproduccion': 'reproduce',
    'reanudar la reproducción': 'reproduce',
    'reanudar la reproduccion': 'reproduce',
    'continúa': 'reproduce',
    'continua': 'reproduce',
    'continúe': 'reproduce',
    'continue': 'reproduce',
    'continúa la reproducción': 'reproduce',
    'continua la reproduccion': 'reproduce',
    'reproducir': 'reproduce',
    'empieza a reproducir': 'reproduce',
    'empiece a reproducir': 'reproduce',
    'inicia la reproducción': 'reproduce',
    'inicia la reproduccion': 'reproduce',
    'pausa la reproducción': 'pausa',
    'pausa la reproduccion': 'pausa',
    'pausar': 'pausa',
    'pon pausa': 'pausa'
  };

  return aliases[normalized] ?? normalized;
}

function commandForIntent(intentName) {
  const commands = {
    'AMAZON.PauseIntent': 'pausa',
    'AMAZON.ResumeIntent': 'reproduce',
    MediaPauseIntent: 'pausa',
    MediaPlayIntent: 'reproduce',
    ShutdownComputerIntent: 'apaga ordenador',
    RestartComputerIntent: 'reinicia ordenador',
    SleepComputerIntent: 'suspende ordenador',
    LockComputerIntent: 'bloquea ordenador'
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

  return speak(command.endsWith('ordenador') ? result.message : 'Hecho.');
}

function extractAssistantMessage(message) {
  const text = String(message ?? '').trim();
  if (!text.toLocaleLowerCase('es-ES').startsWith(ASSISTANT_PREFIX)) {
    return null;
  }

  return text.slice(ASSISTANT_PREFIX.length).trim() || 'Hecho.';
}

function executeRemoteCommand(command) {
  return new Promise((resolve) => {
    const startedAt = Date.now();
    let relayResultLogged = false;
    const logResult = (status, success, error = null) => {
      if (relayResultLogged) {
        return;
      }

      relayResultLogged = true;
      logRelayResult(command, status, success, Date.now() - startedAt, error);
    };
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
      timeout: REQUEST_TIMEOUT_MS
    }, (res) => {
      let data = '';
      res.setEncoding('utf8');
      res.on('data', chunk => data += chunk);
      res.on('end', () => {
        let payload = {};
        try { payload = JSON.parse(data); } catch {}

        if (res.statusCode >= 200 && res.statusCode < 300) {
          logResult(res.statusCode, payload.success === true);
          resolve({
            success: payload.success === true,
            message: payload.message ?? 'No se pudo ejecutar la orden.'
          });
          return;
        }

        logResult(res.statusCode, false);
        resolve({
          success: false,
          message: payload.message ?? 'No he podido comunicarme con el ordenador.'
        });
      });
    });

    req.on('timeout', () => {
      logResult(null, false, 'timeout');
      req.destroy(new Error('timeout'));
    });

    req.on('error', () => {
      logResult(null, false, 'network_error');
      resolve({ success: false, message: 'No he podido conectar con el servicio del ordenador.' });
    });

    req.write(body);
    req.end();
  });
}

function logRelayResult(command, status, success, durationMs, error = null) {
  console.info(JSON.stringify({
    component: 'AlexaPc.Skill',
    event: 'relay_result',
    command,
    status,
    success,
    durationMs,
    error
  }));
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

exports._test = {
  normalizeCommand,
  commandForIntent,
  commandForActionIntent,
  commandForQuestionIntent,
  extractAssistantMessage,
  requestTimeoutMs: REQUEST_TIMEOUT_MS
};
