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

El ordenador iniciará la conexión saliente al relay. No será necesario exponer un puerto del equipo a Internet.

## Mensaje remoto

```json
{
  "type": "execute",
  "requestId": "f4d4f631-5897-47b4-953a-3d4ad5d759dc",
  "command": "pausa"
}
```

Respuesta:

```json
{
  "type": "result",
  "requestId": "f4d4f631-5897-47b4-953a-3d4ad5d759dc",
  "success": true,
  "message": "Reproducción pausada."
}
```

El Worker mantiene una entrada pendiente por `requestId`. El agente procesa mensajes en tareas independientes y serializa únicamente los envíos WebSocket, de modo que un comando exacto no queda bloqueado por una inferencia local. Las inferencias sí se serializan para no competir por GPU; su espera forma parte del límite local de 5 segundos.

Cada petición termina en un mensaje `result`, incluso cuando la ejecución devuelve error, se cancela o lanza una excepción. Si el socket se pierde o no llega ningún resultado, el Durable Object completa y limpia la petición pendiente con un error controlado.

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
