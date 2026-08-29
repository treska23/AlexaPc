# Conectar AlexaPc al relay y a Alexa

## 1. Probar el canal remoto en local

El agente crea automáticamente:

```text
%LOCALAPPDATA%\AlexaPc\relay.json
```

Por defecto contiene:

```json
{
  "enabled": true,
  "relayUrl": "ws://localhost:5184/ws/agent",
  "deviceId": "pc-principal",
  "deviceToken": "dev-device-token"
}
```

Arranca primero `AlexaPc.Relay` y después `AlexaPc.Agent`. En la aplicación debe aparecer:

```text
RELAY · CONECTADO
```

Para probar una orden sin Alexa, ejecuta en PowerShell:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:5184/api/commands" `
  -Headers @{ "X-AlexaPc-Api-Key" = "dev-api-key" } `
  -ContentType "application/json" `
  -Body '{"deviceId":"pc-principal","command":"youtube"}'
```

Si el navegador abre YouTube, el recorrido completo ya funciona:

```text
HTTP -> Relay -> WebSocket -> AlexaPc.Agent -> CommandDispatcher -> Windows
```

## 2. Publicar el relay

Alexa no puede acceder a `localhost`. Para usar voz, `AlexaPc.Relay` debe estar publicado detrás de HTTPS/WSS.

Configura estas variables de entorno en el servidor:

```text
ALEXAPC_DEVICE_TOKEN=<token largo y aleatorio>
ALEXAPC_API_KEY=<clave larga y aleatoria>
```

En el PC cambia `relay.json` a la URL WSS pública y usa el mismo `ALEXAPC_DEVICE_TOKEN`:

```json
{
  "enabled": true,
  "relayUrl": "wss://tu-dominio.example/ws/agent",
  "deviceId": "pc-principal",
  "deviceToken": "TU_TOKEN"
}
```

No publiques el relay usando las claves `dev-*`.

## 3. Crear la Skill

El modelo español está en:

```text
skill/es-ES/interactionModel.json
```

La invocación elegida es:

```text
control pc
```

El código de Lambda está en:

```text
skill/lambda/index.mjs
```

Variables de entorno de Lambda:

```text
RELAY_URL=https://tu-dominio.example
RELAY_API_KEY=TU_API_KEY
DEVICE_ID=pc-principal
```

La Lambda debe añadirse como endpoint de la Custom Skill de Alexa.

## 4. Qué decir exactamente

Una vez la Skill esté enlazada y habilitada:

```text
Alexa, abre control PC.
```

Alexa responderá preguntando qué quieres hacer. Entonces:

```text
abre YouTube
```

También probaremos la invocación en una sola frase cuando la Skill esté instalada:

```text
Alexa, dile a control PC que abra YouTube.
```

Los nombres que Alexa manda deben corresponder a nombres de `commands.json`. El relay nunca manda scripts arbitrarios al PC.
