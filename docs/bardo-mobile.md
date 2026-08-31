# Bardo Mobile

`Bardo.Mobile` es el primer prototipo Android de ControlPCIA/Bardo para convertir un teléfono dedicado en una interfaz de voz permanente para el PC.

## Primer objetivo

El recorrido actual es:

```text
"Bardo"
   -> reconocimiento de voz de Android
   -> comando hablado
   -> HTTP local
   -> AlexaPc.Relay
   -> WebSocket
   -> AlexaPc.Agent
   -> ControlPCIA
   -> Windows
```

También acepta una orden en una sola frase:

```text
Bardo abre YouTube
Bardo pausa
Bardo abre Visual Studio
```

Si solo se dice `Bardo`, el servicio entra en modo comando y escucha la siguiente frase.

## Requisitos de desarrollo

- Visual Studio 2026.
- .NET 10.
- workload de .NET para Android instalado.
- Android 8.0 o posterior en el teléfono.
- El teléfono y el PC en la misma red local.

## Configuración inicial

1. Arranca `AlexaPc.Relay` y `AlexaPc.Agent` en el PC.
2. Averigua la IP local del PC, por ejemplo `192.168.1.2`.
3. Ejecuta `Bardo.Mobile` en el teléfono.
4. Acepta los permisos de micrófono y notificaciones.
5. Configura:

```text
Relay: http://IP_DEL_PC:5184
API key: dev-api-key
Device ID: pc-principal
Palabra de activación: bardo
```

6. Pulsa `Enviar comando de prueba` antes de activar la escucha permanente.
7. Si el test funciona, pulsa `Empezar a escuchar`.

El relay ya escucha en `0.0.0.0:5184`, así que puede recibir conexiones de otros dispositivos de la LAN.

## Estado del prototipo

Esta primera versión utiliza `SpeechRecognizer` de Android con preferencia por reconocimiento offline. Es deliberadamente una primera capa funcional: permite validar teléfono -> relay -> agente -> Windows antes de integrar un motor dedicado de hotword totalmente local.

La siguiente fase sustituirá la escucha continua general por un detector de wake word local más ligero y añadirá arranque automático, estado del PC y respuesta de voz.
