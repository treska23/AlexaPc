const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { pathToFileURL } = require('node:url');
const vm = require('node:vm');

const repoRoot = path.resolve(__dirname, '..', '..');
const modelPath = path.join(repoRoot, 'skill', 'es-ES', 'interactionModel.json');
const templatePath = path.join(repoRoot, 'skill', 'alexa-hosted', 'index.template.js');
const lambdaModulePath = path.join(repoRoot, 'skill', 'lambda', 'index.mjs');
const prepareScriptPath = path.join(repoRoot, 'scripts', 'Prepare-AlexaSkill.ps1');
const workerPath = path.join(repoRoot, 'cloudflare', 'worker', 'src', 'index.js');
const llamaServicePath = path.join(repoRoot, 'src', 'AlexaPc.Agent', 'Services', 'LocalLlamaService.cs');

function readStrictUtf8(filePath) {
  const bytes = fs.readFileSync(filePath);
  const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  const markers = [
    String.fromCodePoint(0xfffd),
    String.fromCodePoint(0x00c3),
    String.fromCodePoint(0x00c2),
    String.fromCodePoint(0x00e2, 0x20ac),
    String.fromCodePoint(0x00ef, 0x00bf, 0x00bd)
  ];

  for (const marker of markers) {
    assert.equal(text.includes(marker), false, `Possible mojibake in ${filePath}`);
  }

  return text.replace(/^\uFEFF/, '');
}

function loadAlexaHostedTemplate() {
  const logs = [];
  const source = readStrictUtf8(templatePath)
    .replace('__RELAY_URL_JSON__', JSON.stringify('https://relay.example.test'))
    .replace('__RELAY_API_KEY_JSON__', JSON.stringify('test-key'))
    .replace('__DEVICE_ID_JSON__', JSON.stringify('test-device'));
  const module = { exports: {} };
  const context = {
    Buffer,
    URL,
    console: {
      info(line) {
        logs.push(JSON.parse(line));
      }
    },
    exports: module.exports,
    module,
    require(name) {
      if (name === 'https') {
        return {
          request() {
            const handlers = {};
            return {
              destroy(error) {
                handlers.error?.(error);
              },
              end() {
                handlers.error?.(new Error('simulated offline relay'));
              },
              on(name, handler) {
                handlers[name] = handler;
                return this;
              },
              write() {}
            };
          }
        };
      }

      return require(name);
    }
  };

  vm.runInNewContext(source, context, { filename: templatePath });
  return { exports: module.exports, logs };
}

test('interaction model supports documented and natural Spanish one-shot actions', () => {
  const model = JSON.parse(readStrictUtf8(modelPath));
  const languageModel = model.interactionModel.languageModel;
  const intents = new Map(languageModel.intents.map(item => [item.name, item]));
  const intent = intents.get('ExecuteCommandIntent');

  assert.equal(languageModel.invocationName, 'bardo control');
  assert.equal(intent.slots[0].type, 'AMAZON.SearchQuery');
  assert.ok(intent.samples.includes('quiero que {command}'));
  assert.ok(intent.samples.includes('quiero {command}'));
  assert.ok(intent.samples.includes('necesito que {command}'));
  assert.ok(intent.samples.includes('necesito {command}'));
  assert.ok(intent.samples.includes('por favor {command}'));
  assert.equal(intent.samples.includes('abre {command}'), false);
  assert.equal(intent.samples.includes('busca {command}'), false);

  assert.equal(intents.get('OpenComputerIntent').slots[0].type, 'AMAZON.SearchQuery');
  assert.ok(intents.get('OpenComputerIntent').samples.includes('abre {target}'));
  assert.ok(intents.get('CloseComputerIntent').samples.includes('cierra {target}'));
  assert.ok(intents.get('SearchComputerIntent').samples.includes('busca {query}'));
  assert.ok(intents.get('MaximizeWindowIntent').samples.includes('maximiza {target}'));
  assert.ok(intents.get('BringWindowIntent').samples.includes('trae {target} al frente'));
  assert.ok(intents.get('WhatComputerIntent').samples.includes('qué {query}'));

  const whatIntent = intents.get('WhatIsIntent');
  const whyIntent = intents.get('WhyQuestionIntent');
  assert.equal(whatIntent.slots[0].type, 'AMAZON.SearchQuery');
  assert.ok(whatIntent.samples.includes('qué es {topic}'));
  assert.ok(whyIntent.samples.includes('por qué {subject}'));

  assert.ok(intents.has('AMAZON.PauseIntent'));
  assert.ok(intents.has('AMAZON.ResumeIntent'));
  assert.ok(intents.get('MediaPlayIntent').samples.includes('reproduce'));
  assert.ok(intents.get('ShutdownComputerIntent').samples.includes('apaga el ordenador'));
  assert.ok(intents.get('RestartComputerIntent').samples.includes('reinicia el ordenador'));
  assert.ok(intents.get('SleepComputerIntent').samples.includes('suspende el ordenador'));
  assert.ok(intents.get('LockComputerIntent').samples.includes('bloquea el ordenador'));

  for (const sample of intent.samples) {
    assert.notEqual(sample.trim(), '{command}', 'AMAZON.SearchQuery needs a carrier phrase');
  }
});

test('normalization keeps command aliases compatible with commands.json', () => {
  const { exports } = loadAlexaHostedTemplate();

  assert.equal(exports._test.normalizeCommand('  You   Tube '), 'youtube');
  assert.equal(exports._test.normalizeCommand('EL BLOC DE NOTAS'), 'bloc de notas');
  assert.equal(exports._test.normalizeCommand('Reinicia el PC'), 'reinicia ordenador');
  assert.equal(exports._test.normalizeCommand('Reanuda la reproducción'), 'reproduce');
  assert.equal(exports._test.normalizeCommand('Pon pausa'), 'pausa');
  assert.equal(exports._test.normalizeCommand('mi comando personalizado'), 'mi comando personalizado');
  assert.equal(exports._test.commandForIntent('ShutdownComputerIntent'), 'apaga ordenador');
  assert.equal(exports._test.commandForIntent('AMAZON.PauseIntent'), 'pausa');
  assert.equal(exports._test.commandForIntent('AMAZON.HelpIntent'), null);
  assert.equal(
    exports._test.commandForActionIntent({
      name: 'OpenComputerIntent',
      slots: { target: { value: 'Google Chrome' } }
    }),
    'abre Google Chrome'
  );
  assert.equal(
    exports._test.commandForActionIntent({
      name: 'BringWindowIntent',
      slots: { target: { value: 'Microsoft Edge' } }
    }),
    'trae Microsoft Edge al frente'
  );
  assert.equal(
    exports._test.commandForActionIntent({
      name: 'WhatComputerIntent',
      slots: { query: { value: 'programas tengo abiertos' } }
    }),
    'qué programas tengo abiertos'
  );
  assert.equal(
    exports._test.commandForQuestionIntent({
      name: 'WhatIsIntent',
      slots: { topic: { value: 'un agujero negro' } }
    }),
    'qué es un agujero negro'
  );
  assert.equal(exports._test.extractAssistantMessage('[bardo] He encontrado una opción.'), 'He encontrado una opción.');
  assert.equal(exports._test.extractAssistantMessage('Acción ejecutada.'), null);
});

test('environment-based Lambda keeps the same normalization behavior', async () => {
  const lambda = await import(pathToFileURL(lambdaModulePath).href);

  assert.equal(lambda.normalizeCommand('  You   Tube '), 'youtube');
  assert.equal(lambda.normalizeCommand('Apaga el PC'), 'apaga ordenador');
  assert.equal(lambda.normalizeCommand('mi comando personalizado'), 'mi comando personalizado');
  assert.equal(lambda.commandForIntent('SleepComputerIntent'), 'suspende ordenador');
  assert.equal(
    lambda.commandForActionIntent({
      name: 'SearchComputerIntent',
      slots: { query: { value: 'vídeos de jazz' } }
    }),
    'busca vídeos de jazz'
  );
  assert.equal(
    lambda.commandForQuestionIntent({
      name: 'WhyQuestionIntent',
      slots: { subject: { value: 'el cielo es azul' } }
    }),
    'por qué el cielo es azul'
  );
  assert.equal(lambda.extractAssistantMessage('[bardo] Respuesta local.'), 'Respuesta local.');
  assert.equal(lambda.requestTimeoutMs, 7200);
});

test('timeouts are nested inside the Alexa response budget', async () => {
  const { exports } = loadAlexaHostedTemplate();
  const workerSource = readStrictUtf8(workerPath);
  const llamaSource = readStrictUtf8(llamaServicePath);
  const workerTimeoutMs = Number(workerSource.match(/COMMAND_TIMEOUT_MS\s*=\s*(\d+)/)?.[1]);
  const assistantTimeoutSeconds = Number(
    llamaSource.match(/MaximumInferenceTimeoutSeconds\s*=\s*(\d+)/)?.[1]
  );

  assert.equal(assistantTimeoutSeconds, 5);
  assert.equal(workerTimeoutMs, 6200);
  assert.equal(exports._test.requestTimeoutMs, 7200);
  assert.ok(assistantTimeoutSeconds * 1000 < workerTimeoutMs);
  assert.ok(workerTimeoutMs < exports._test.requestTimeoutMs);
  assert.ok(exports._test.requestTimeoutMs < 8000);
});

test('Ollama requests disable hidden thinking and keep the selected model warm', () => {
  const source = readStrictUtf8(llamaServicePath);

  assert.match(source, /think\s*=\s*false/);
  assert.match(source, /OllamaKeepAlive\s*=\s*"24h"/);
  assert.match(source, /WarmUpAsync/);
});

test('LaunchRequest is logged without configuration or secrets', async () => {
  const { exports, logs } = loadAlexaHostedTemplate();
  const response = await exports.handler({
    request: {
      type: 'LaunchRequest',
      requestId: 'request-1',
      locale: 'es-ES'
    }
  });

  assert.match(response.response.outputSpeech.text, /Bardo Control listo/);
  assert.equal(response.response.shouldEndSession, false);
  assert.deepEqual(logs[0], {
    component: 'AlexaPc.Skill',
    event: 'request',
    requestType: 'LaunchRequest',
    requestId: 'request-1',
    locale: 'es-ES',
    intentName: null,
    command: null
  });
  assert.equal(JSON.stringify(logs).includes('test-key'), false);
  assert.equal(JSON.stringify(logs).includes('relay.example.test'), false);
});

test('IntentRequest logs the intent and received command slot', async () => {
  const { exports, logs } = loadAlexaHostedTemplate();
  const response = await exports.handler({
    request: {
      type: 'IntentRequest',
      requestId: 'request-2',
      locale: 'es-ES',
      intent: {
        name: 'ExecuteCommandIntent',
        slots: { command: { value: '  YouTube  ' } }
      }
    }
  });

  assert.match(response.response.outputSpeech.text, /No he podido conectar/);
  assert.equal(logs[0].requestType, 'IntentRequest');
  assert.equal(logs[0].intentName, 'ExecuteCommandIntent');
  assert.equal(logs[0].command, 'YouTube');
});

test('fixed computer intents do not depend on the open search slot', async () => {
  const { exports, logs } = loadAlexaHostedTemplate();
  const response = await exports.handler({
    request: {
      type: 'IntentRequest',
      requestId: 'request-shutdown',
      locale: 'es-ES',
      intent: {
        name: 'ShutdownComputerIntent',
        slots: {}
      }
    }
  });

  assert.match(response.response.outputSpeech.text, /ordenador/);
  assert.doesNotMatch(response.response.outputSpeech.text, /\bPC\b/i);
  assert.equal(logs[0].intentName, 'ShutdownComputerIntent');
});

test('skill sources and preparation script are strict UTF-8 without mojibake', () => {
  readStrictUtf8(modelPath);
  readStrictUtf8(templatePath);
  readStrictUtf8(lambdaModulePath);
  readStrictUtf8(prepareScriptPath);
});
