# AlexaPc / Bardo

Asistente local para Windows controlado por voz mediante Alexa.

El proyecto empezó como un puente de comandos entre Alexa y el ordenador. La dirección actual es convertirlo en **Bardo**, un asistente local que entiende lenguaje natural, elige herramientas seguras y ejecuta flujos de trabajo en el PC sin depender de APIs de pago.

## Arquitectura actual

```text
Alexa
  -> Bardo Control
  -> Cloudflare relay
  -> AlexaPc.Agent
  -> comando exacto? -> ejecución inmediata
  -> orden de energía? -> catálogo protegido
  -> texto libre? -> AlexaPc.Control
                  -> control determinista de Windows
                  -> traducción local con Qwen cuando hace falta
                  -> respuesta breve por Alexa
```

Componentes:

- `AlexaPc.Agent`: aplicación WPF que ejecuta acciones en Windows y aloja el cerebro local.
- `AlexaPc.Control`: motor integrado de ControlPCIA para aplicaciones, ventanas, pantallas, multimedia, web y órdenes compuestas.
- `AlexaPc.Relay`: relay HTTP/WebSocket para desarrollo local.
- Cloudflare Worker: relay público persistente entre Alexa y el ordenador.
- Alexa Custom Skill `Bardo Control`.
- Integración local con Ollama, LM Studio o llama.cpp.

### Implementado

- WPF sobre .NET 10 y MVVM.
- Configuración de comandos mediante `commands.json`.
- Apertura de programas y URLs.
- Play, pausa, siguiente/anterior, volumen y mute.
- Bloquear, suspender, apagar y reiniciar Windows.
- Icono propio, bandeja del sistema y arranque con Windows.
- Logs en `%LOCALAPPDATA%\AlexaPc\logs`.
- Relay WebSocket persistente con reconexión automática.
- Modelo de Skill en español (`bardo control`).
- Lambda puente entre Alexa y el relay.
- Control general del ordenador con lenguaje natural mediante el motor integrado de ControlPCIA.
- Detección automática de Ollama, LM Studio y llama.cpp.
- Apertura y cierre de aplicaciones, gestión de ventanas y pantallas, multimedia, búsquedas web y órdenes compuestas.
- Precarga de inventario y del traductor local para evitar latencia fría.
- Reconstrucción del verbo en la Skill para que `abre Chrome` no llegue al ordenador como solo `Chrome`.
- Respuestas breves del controlador local leídas por Alexa.
- Validación de las acciones traducidas y protección adicional para acciones de energía.
- Build automática con GitHub Actions.
- Pruebas de integración del Worker con WebSocket, Durable Objects, concurrencia y timeout.

## Ejecutar el agente

1. Clona el repositorio.
2. Abre `AlexaPc.sln` en Visual Studio 2026.
3. Establece `AlexaPc.Agent` como proyecto de inicio.
4. Ejecuta con `F5`.

La primera vez se crean automáticamente:

```text
%LOCALAPPDATA%\AlexaPc\commands.json
%LOCALAPPDATA%\AlexaPc\relay.json
%LOCALAPPDATA%\AlexaPc\assistant.json
```

La configuración de Llama local está documentada en [`docs/local-assistant.md`](docs/local-assistant.md).

## Probar el canal remoto localmente

Arranca `AlexaPc.Relay` y luego `AlexaPc.Agent`. El badge superior debe pasar a:

```text
RELAY · CONECTADO
```

Después ejecuta en PowerShell:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:5184/api/commands" `
  -Headers @{ "X-AlexaPc-Api-Key" = "dev-api-key" } `
  -ContentType "application/json" `
  -Body '{"deviceId":"pc-principal","command":"youtube"}'
```

Si se abre YouTube, funciona el recorrido básico:

```text
HTTP -> Relay -> WebSocket -> Agent -> CommandDispatcher -> Windows
```

Una petición que no coincida con un nombre de `commands.json` pasa al controlador general integrado. Por ejemplo:

```text
abre Chrome
busca vídeos de jazz en YouTube
maximiza Spotify y baja el volumen
qué programas tengo abiertos
```

## Alexa

La configuración completa está en [`docs/alexa-setup.md`](docs/alexa-setup.md).

La invocación de la Skill es:

```text
bardo control
```

Ejemplo directo:

```text
Alexa, abre Bardo Control.
```

Y después puede usarse un comando clásico:

```text
abre YouTube
```

O lenguaje más natural:

```text
quiero que abras YouTube
necesito que bajes el volumen
trae Spotify al frente y pausa la reproducción
```

También puede usarse en una sola frase:

```text
Alexa, dile a Bardo Control que abra YouTube.
Alexa, dile a Bardo Control que quiero ver YouTube.
```

La elección del nombre de dos palabras y el diagnóstico de las invocaciones están documentados en [`docs/alexa-one-shot.md`](docs/alexa-one-shot.md).

## Seguridad

Las órdenes conocidas usan controladores deterministas. Si una orden necesita traducción local, el modelo solo propone un plan que pasa por los validadores de ControlPCIA antes de ejecutarse; se bloquean operaciones destructivas sobre archivos y discos.

Las acciones de apagar, reiniciar, suspender y bloquear no pasan por el controlador general: siguen en el catálogo protegido y requieren una petición explícita del usuario.

## Proyecto unificado

AlexaPc ya contiene el núcleo estable de ControlPCIA como biblioteca. No hace falta instalar ni mantener un segundo ejecutable para que las órdenes de Alexa controlen Windows. El servidor móvil y la APK de ControlPCIA quedan fuera de esta integración porque no forman parte del recorrido Alexa → ordenador.

El objetivo es que Alexa sea la interfaz de voz y Bardo/ControlPCIA el controlador local del ordenador.
