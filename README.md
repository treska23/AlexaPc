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
  -> texto libre? -> Llama local
                  -> respuesta breve por Alexa
                  -> o herramientas autorizadas en Windows
```

Componentes:

- `AlexaPc.Agent`: aplicación WPF que ejecuta acciones en Windows y aloja el cerebro local.
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
- Interpretación de lenguaje natural mediante Llama local.
- Detección automática de Ollama, LM Studio y llama.cpp.
- Respuestas breves de Llama leídas por Alexa.
- Selección de hasta cuatro herramientas autorizadas para peticiones compuestas.
- Rechazo de herramientas inventadas y protección adicional para acciones de energía.
- Build automática con GitHub Actions.

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

Una petición que no coincida con un nombre de `commands.json` pasa al asistente local. Por ejemplo, con Ollama/LM Studio/llama.cpp arrancado:

```text
quiero ver YouTube
abre YouTube y baja el volumen
dime qué es un agujero negro
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
quiero ver YouTube
necesito bajar el volumen
dime qué es un agujero negro
```

También puede usarse en una sola frase:

```text
Alexa, dile a Bardo Control que abra YouTube.
Alexa, dile a Bardo Control que quiero ver YouTube.
```

La elección del nombre de dos palabras y el diagnóstico de las invocaciones están documentados en [`docs/alexa-one-shot.md`](docs/alexa-one-shot.md).

## Seguridad

Llama no recibe capacidad para ejecutar código arbitrario. Solo puede elegir nombres existentes en `%LOCALAPPDATA%\AlexaPc\commands.json`.

Las acciones de apagar, reiniciar, suspender y bloquear requieren además una petición explícita del usuario; una inferencia indirecta del modelo no basta.

## Dirección del proyecto

La siguiente fase es ampliar el catálogo de herramientas seguras de Bardo:

- abrir soluciones y proyectos de trabajo;
- buscar archivos y consultar Git;
- abrir proyectos concretos de ChatGPT en el navegador;
- crear rutinas compuestas como `voy a dibujar` o `voy a tocar la batería`;
- memoria local;
- estado del sistema y procesos;
- selección inteligente de herramientas por Llama.

El objetivo es que Alexa sea la interfaz de voz y Bardo/Llama el cerebro local del ordenador.
