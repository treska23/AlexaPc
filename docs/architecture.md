# Arquitectura

## Flujo previsto

```text
Voz
  ↓
Alexa
  ↓
Alexa Skill
  ↓
AlexaPc.Relay
  ↓  WebSocket seguro y persistente
AlexaPc.Agent (Windows)
  ↓
CommandDispatcher
  ↓
Windows
```

## Por qué separar Relay y Agent

El agente de Windows no debe depender de la implementación concreta de Alexa. El `CommandDispatcher` recibe un nombre de comando y lo ejecuta. Hoy puede invocarlo la interfaz local; mañana lo invocará un mensaje WebSocket sin cambiar la lógica que controla Windows.

El PC iniciará la conexión saliente al relay. No será necesario exponer un puerto del equipo a Internet.

## Mensaje remoto propuesto

```json
{
  "id": "f4d4f631-5897-47b4-953a-3d4ad5d759dc",
  "command": "pausa",
  "sentAtUtc": "2026-08-29T16:00:00Z"
}
```

Respuesta:

```json
{
  "id": "f4d4f631-5897-47b4-953a-3d4ad5d759dc",
  "success": true,
  "message": "Acción ejecutada: media.playPause"
}
```

## Seguridad prevista

- TLS en todo el transporte remoto.
- Identificador propio para cada agente.
- Token de dispositivo revocable.
- Lista blanca de comandos: el relay envía nombres, no código arbitrario.
- El agente solo ejecuta comandos existentes en su `commands.json`.

## Próxima fase

1. Añadir ejecución en bandeja y arranque con Windows.
2. Crear `AlexaPc.Relay`.
3. Conectar el agente al relay mediante WebSocket.
4. Implementar la Alexa Skill.
5. Añadir Wake-on-LAN desde Alexa para el encendido en frío.
