# AlexaPc

Agente de Windows para controlar el PC mediante comandos de voz de Alexa.

## Estado actual

AlexaPc ya tiene dos capas funcionales:

- `AlexaPc.Agent`: aplicación WPF que ejecuta los comandos en Windows.
- `AlexaPc.Relay`: relay HTTP/WebSocket que recibe una orden remota y la envía al PC conectado.

También se incluye el modelo `es-ES` de la Custom Skill y una Lambda Node.js que convierte la orden de voz en una llamada al relay.

### Implementado

- WPF sobre .NET 10 y MVVM.
- Configuración de comandos mediante `commands.json`.
- Apertura de programas y URLs.
- Play/Pause, pista siguiente/anterior, volumen y mute.
- Bloquear, suspender, apagar y reiniciar Windows.
- Icono propio integrado en el ejecutable y la barra de tareas.
- Creación automática del acceso directo `AlexaPc.lnk` en el escritorio.
- `relay.json` para configurar el transporte remoto.
- Cliente WebSocket persistente con reconexión automática.
- `AlexaPc.Relay` con autenticación de dispositivo y API key.
- Respuesta de resultado del PC al relay.
- Modelo de Skill en español (`control pc`).
- Lambda puente entre Alexa y el relay.
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
```

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

Si se abre YouTube, funciona el recorrido:

```text
HTTP -> Relay -> WebSocket -> Agent -> CommandDispatcher -> Windows
```

## Alexa

La configuración completa está en [`docs/alexa-setup.md`](docs/alexa-setup.md).

La invocación de la Skill es:

```text
control pc
```

Cuando la Skill esté desplegada y habilitada:

```text
Alexa, abre control PC.
```

Y después:

```text
abre YouTube
```

El agente solo ejecuta nombres existentes en `commands.json`; Alexa no puede mandar código arbitrario al equipo.

## Siguiente fase

- Publicar `AlexaPc.Relay` detrás de HTTPS/WSS.
- Crear y enlazar la Custom Skill en Amazon Developer.
- Probar la invocación en una sola frase.
- Añadir bandeja del sistema y arranque automático con Windows.
- Añadir Wake-on-LAN para encender el PC cuando esté apagado.
