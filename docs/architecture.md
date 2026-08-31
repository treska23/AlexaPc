# Arquitectura

## Flujo actual

```text
Voz
  ↓
Alexa Skill · Bardo Control
  ↓ HTTPS
Cloudflare Worker
  ↓ WebSocket persistente
AlexaPc.Agent
  ├─ comando exacto → CommandExecutionService
  ├─ energía → catálogo protegido
  └─ lenguaje natural → AlexaPc.Control
                         ├─ control determinista
                         └─ traducción local con Qwen
  ↓
Windows
```

El ordenador inicia la conexión saliente al relay; no expone ningún puerto a Internet. El Worker correlaciona cada orden y su respuesta mediante un `requestId` y limpia la petición aunque haya un error o timeout.

## Proyecto unificado

`AlexaPc.Control` contiene dentro de esta solución el núcleo estable de ControlPCIA. Se enlaza como biblioteca con `AlexaPc.Agent`, por lo que Alexa no lanza ni se comunica con un segundo ejecutable.

El servidor móvil y la APK del proyecto original quedan fuera de esta integración. El ejecutable independiente de ControlPCIA puede seguir abierto para sus propios usos, pero AlexaPc no depende de él.

## Interpretación

La Skill reconstruye verbos que `AMAZON.SearchQuery` no incluye en el valor del slot. Por ejemplo:

```text
OpenComputerIntent + «Chrome»
  → «abre Chrome»

WhatComputerIntent + «programas tengo abiertos»
  → «qué programas tengo abiertos»
```

El agente mantiene primero el catálogo exacto para acciones inmediatas y de energía. El resto pasa al controlador general: aplicaciones, web, ventanas, pantallas, multimedia, consultas y órdenes encadenadas. Las rutas conocidas no consultan ningún modelo; Qwen solo traduce localmente lo que el núcleo determinista no reconoce.

## Mensaje remoto

```json
{
  "type": "execute",
  "requestId": "f4d4f631-5897-47b4-953a-3d4ad5d759dc",
  "command": "maximiza Spotify y baja el volumen"
}
```

Respuesta:

```json
{
  "type": "result",
  "requestId": "f4d4f631-5897-47b4-953a-3d4ad5d759dc",
  "success": true,
  "message": "[bardo] He completado las dos acciones."
}
```

## Concurrencia y tiempos

El agente procesa mensajes remotos en tareas independientes y serializa únicamente los envíos WebSocket. Una orden exacta no queda bloqueada por una traducción local. Los límites están anidados para devolver un error antes de que Alexa agote su presupuesto:

```text
control local:       4.800 ms
Cloudflare Worker:   6.200 ms
Lambda:              7.200 ms
Alexa:               menos de 8.000 ms
```

## Seguridad

- TLS en todo el transporte remoto.
- API key del relay y token de dispositivo revocables.
- Ninguna clave privada se guarda en Git.
- El relay transporta texto, nunca código generado por Internet.
- Las órdenes conocidas usan controladores deterministas.
- Las traducciones locales pasan por validadores que bloquean eliminaciones, movimientos, cortes, desinstalaciones y operaciones destructivas de disco.
- Apagar, reiniciar, suspender y bloquear requieren una petición explícita y permanecen fuera del controlador general.
