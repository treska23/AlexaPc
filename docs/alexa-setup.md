# Conectar AlexaPc a Alexa

## 1. Canal local — ya probado

Con `AlexaPc Local` arrancado, el recorrido local es:

```text
HTTP -> Relay local -> WebSocket -> AlexaPc.Agent -> CommandDispatcher -> Windows
```

Si una petición a `/api/commands` abre YouTube, esta parte está terminada.

## 2. Crear el relay cloud estable

Alexa no puede acceder a `localhost`. Para uso real, AlexaPc usa un relay en Cloudflare Workers + Durable Objects. El Worker está en:

```text
cloudflare/worker
```

Hay un script que despliega el Worker, crea dos claves aleatorias, guarda los secretos en Cloudflare y cambia automáticamente `%LOCALAPPDATA%\AlexaPc\relay.json` para que el agente use WSS:

```powershell
.\scripts\Deploy-CloudRelay.ps1
```

Requisitos:

- Node.js/npm instalado.
- Una cuenta gratuita de Cloudflare. La primera ejecución abre el navegador para iniciar sesión.

Al terminar, el script muestra una URL estable similar a:

```text
https://alexapc-relay.<tu-subdominio>.workers.dev
```

y configura el PC con:

```text
wss://alexapc-relay.<tu-subdominio>.workers.dev/ws/agent
```

La configuración privada necesaria para la Skill queda solamente en este PC:

```text
%LOCALAPPDATA%\AlexaPc\cloud-relay.json
```

No se suben las claves al repositorio.

Después de ejecutar el script, reinicia AlexaPc. La aplicación debe volver a marcar:

```text
RELAY · CONECTADO
```

## 3. Probar el relay cloud

Abre `%LOCALAPPDATA%\AlexaPc\cloud-relay.json`. Contiene `relayUrl`, `apiKey` y `deviceId`.

Prueba primero:

```powershell
Invoke-RestMethod "https://TU-WORKER.workers.dev/health"
```

Después prueba una orden usando la `apiKey` guardada:

```powershell
Invoke-RestMethod -Method Post -Uri "https://TU-WORKER.workers.dev/api/commands" -Headers @{"X-AlexaPc-Api-Key"="TU_API_KEY"} -ContentType "application/json" -Body '{"deviceId":"pc-principal","command":"youtube"}'
```

Si abre YouTube, ya funciona también desde Internet:

```text
Internet -> Cloudflare Worker -> WebSocket -> AlexaPc.Agent -> Windows
```

## 4. Crear la Skill

El modelo español está en:

```text
skill/es-ES/interactionModel.json
```

La invocación actual es:

```text
bardo control
```

El código de Lambda está en:

```text
skill/lambda/index.mjs
```

Variables de entorno de la Skill/Lambda:

```text
RELAY_URL=https://TU-WORKER.workers.dev
RELAY_API_KEY=<apiKey de cloud-relay.json>
DEVICE_ID=pc-principal
```

El modelo usa `AMAZON.SearchQuery` para capturar nombres de comandos abiertos como `YouTube`, `bloc de notas`, etc. Este tipo está soportado en `es-ES`; cada muestra conserva una frase portadora (`abre`, `ejecuta`, etc.), como exige Amazon.

## 5. Qué decir exactamente

Una vez la Skill esté enlazada y habilitada:

```text
Alexa, abre Bardo Control.
```

Alexa responderá preguntando qué quieres hacer. Entonces:

```text
abre YouTube
```

También puede usarse una sola frase:

```text
Alexa, dile a Bardo Control que abra YouTube.
```

También está soportado:

```text
Alexa, abre Bardo Control y abre YouTube.
```

No se debe prometer `Alexa, Bardo abre YouTube`: la interacción sin nombre de
Skill solo está disponible para `en-US`, no para `es-ES`. Consulta el diagnóstico
completo en [`alexa-one-shot.md`](alexa-one-shot.md).

## 6. Preparar y validar lo que se pega en Alexa

El script es compatible con Windows PowerShell 5.1. Lee UTF-8 de forma estricta,
rechaza patrones de mojibake, escribe el código generado como UTF-8 sin BOM y
comprueba que el portapapeles devuelve exactamente el mismo texto.

Valida primero sin escribir archivos ni cambiar el portapapeles:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Prepare-AlexaSkill.ps1 -Copy Model -ValidateOnly
powershell -ExecutionPolicy Bypass -File .\scripts\Prepare-AlexaSkill.ps1 -Copy Code -ValidateOnly
```

Después copia el modelo y el código:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Prepare-AlexaSkill.ps1 -Copy Model
powershell -ExecutionPolicy Bypass -File .\scripts\Prepare-AlexaSkill.ps1 -Copy Code
```

En Alexa Developer Console hay que actualizar ambos:

1. `Build > Interaction Model > JSON Editor`: pega el modelo, guarda y ejecuta
   `Build Model`.
2. `Code > lambda > index.js`: pega el código, pulsa `Save` y después `Deploy`.

La Lambda emite una línea JSON por petición con `requestType`, `intentName` y
`command`. No registra la URL del relay, la API key ni el identificador del
dispositivo.

Las pruebas locales de modelo, normalización, logging y codificación se ejecutan
sin dependencias adicionales:

```powershell
node --test .\skill\tests\alexa-skill.test.cjs
```

Los nombres recibidos deben corresponder a comandos de `commands.json`. AlexaPc nunca acepta código arbitrario desde Internet.
