# AlexaPc

Agente de Windows para controlar el PC mediante comandos de voz de Alexa.

## Estado actual

La primera versión ya contiene el núcleo local. Antes de conectar Alexa podemos probar cada acción directamente en Windows, lo que permite separar problemas del PC de problemas de la Skill o del transporte remoto.

### Ya implementado

- WPF sobre .NET 10.
- MVVM sin lógica de aplicación en el code-behind de la ventana.
- Configuración de comandos mediante JSON.
- Apertura de programas y archivos ejecutables.
- Apertura de URLs.
- Play/Pause, pista siguiente/anterior.
- Subir/bajar volumen y mute.
- Bloquear Windows.
- Suspender, apagar y reiniciar.
- Interfaz local para ejecutar y validar comandos.
- Build automática con GitHub Actions.

## Ejecutar

1. Clona el repositorio.
2. Abre `AlexaPc.sln` en Visual Studio 2026.
3. Establece `AlexaPc.Agent` como proyecto de inicio.
4. Ejecuta con `F5`.

La primera vez se crea automáticamente:

```text
%LOCALAPPDATA%\AlexaPc\commands.json
```

La aplicación incluye un botón para abrir ese archivo. Después de modificarlo, pulsa **Recargar**.

## Comandos personalizados

Ejemplo para abrir un programa:

```json
{
  "name": "bloc de notas",
  "description": "Abre el Bloc de notas.",
  "type": "process",
  "target": "notepad.exe"
}
```

Con argumentos:

```json
{
  "name": "mi carpeta",
  "description": "Abre una carpeta concreta.",
  "type": "process",
  "target": "explorer.exe",
  "arguments": "C:\\Users\\TuUsuario\\Documents"
}
```

Una URL:

```json
{
  "name": "youtube",
  "description": "Abre YouTube.",
  "type": "url",
  "target": "https://www.youtube.com"
}
```

Las variables de entorno de Windows funcionan en `target` y `arguments`, por ejemplo `%USERPROFILE%`.

## Acciones integradas

Los comandos de tipo `builtIn` aceptan actualmente estos valores:

```text
media.playPause
media.next
media.previous
volume.mute
volume.up
volume.down
system.lock
system.sleep
system.shutdown
system.restart
```

## Arquitectura prevista

```text
"Alexa, abre DaVinci"
        │
        ▼
      Alexa
        │
        ▼
   Alexa Skill
        │
        ▼
  AlexaPc.Relay
        │ WebSocket/TLS
        ▼
  AlexaPc.Agent
        │
        ▼
 CommandDispatcher
        │
        ▼
      Windows
```

El PC iniciará una conexión saliente persistente. No será necesario abrir puertos del router ni exponer directamente Windows a Internet.

Alexa enviará **nombres de comandos**, no scripts arbitrarios. El agente solamente ejecutará acciones que existan en su configuración local.

Más detalle en [`docs/architecture.md`](docs/architecture.md).

## Siguiente fase

- Bandeja del sistema y arranque automático con Windows.
- Edición visual de comandos desde la propia aplicación.
- `AlexaPc.Relay` con WebSocket autenticado.
- Conexión persistente del agente al relay.
- Alexa Skill.
- Wake-on-LAN para encender el PC cuando esté apagado.
