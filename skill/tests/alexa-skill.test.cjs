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

test('interaction model supports the documented Spanish one-shot action', () => {
  const model = JSON.parse(readStrictUtf8(modelPath));
  const languageModel = model.interactionModel.languageModel;
  const intent = languageModel.intents.find(({ name }) => name === 'ExecuteCommandIntent');

  assert.equal(languageModel.invocationName, 'bardo control');
  assert.equal(intent.slots[0].type, 'AMAZON.SearchQuery');
  assert.ok(intent.samples.includes('abra {command}'));
  assert.ok(intent.samples.includes('abre {command}'));
  assert.equal(intent.samples.includes('que abra {command}'), false);

  for (const sample of intent.samples) {
    assert.notEqual(sample.trim(), '{command}', 'AMAZON.SearchQuery needs a carrier phrase');
  }
});

test('normalization keeps command aliases compatible with commands.json', () => {
  const { exports } = loadAlexaHostedTemplate();

  assert.equal(exports._test.normalizeCommand('  You   Tube '), 'youtube');
  assert.equal(exports._test.normalizeCommand('EL BLOC DE NOTAS'), 'bloc de notas');
  assert.equal(exports._test.normalizeCommand('Reinicia el PC'), 'reinicia ordenador');
  assert.equal(exports._test.normalizeCommand('mi comando personalizado'), 'mi comando personalizado');
});

test('environment-based Lambda keeps the same normalization behavior', async () => {
  const lambda = await import(pathToFileURL(lambdaModulePath).href);

  assert.equal(lambda.normalizeCommand('  You   Tube '), 'youtube');
  assert.equal(lambda.normalizeCommand('Apaga el PC'), 'apaga ordenador');
  assert.equal(lambda.normalizeCommand('mi comando personalizado'), 'mi comando personalizado');
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

test('skill sources and preparation script are strict UTF-8 without mojibake', () => {
  readStrictUtf8(modelPath);
  readStrictUtf8(templatePath);
  readStrictUtf8(lambdaModulePath);
  readStrictUtf8(prepareScriptPath);
});
