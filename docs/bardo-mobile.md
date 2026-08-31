# Bardo Mobile

`Bardo.Mobile` convierte un teléfono Android dedicado en la interfaz de voz permanente de ControlPCIA/Bardo para el PC.

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

La activación y la orden se hacen en dos pasos para no perder el principio del comando al cambiar de sesión de reconocimiento:

```text
Bardo
abre YouTube
```

Al oír `Bardo`, el servicio entra en modo comando y escucha la siguiente frase.

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

6. La escucha comienza automáticamente al abrir la aplicación. Usa `Enviar comando de prueba` para comprobar el enlace sin depender del micrófono.

El relay ya escucha en `0.0.0.0:5184`, así que puede recibir conexiones de otros dispositivos de la LAN.

## Uso por voz

La forma fiable de uso es activar primero a Bardo y decir la orden después:

```text
Bardo
abre YouTube
```

El PC admite órdenes compuestas dentro de las funciones implementadas. Por ejemplo:

```text
Bardo
abre Chrome, busca vídeos de jazz en YouTube, coloca Chrome en la pantalla de la derecha y baja el volumen
```

También controla aplicaciones y ventanas, distribución de pantallas, reproducción multimedia, volumen y consultas sobre lo que está abierto en el PC. Las acciones destructivas no se ejecutan por traducción libre.

## Modo dedicado 0.2.1

La aplicación incluye:

- icono propio de Bardo;
- arranque automático de la escucha al abrirse;
- receptor de arranque del teléfono, que intenta iniciar directamente la voz y la interfaz;
- actividad `HOME`, para poder sustituir el lanzador de Android;
- funcionamiento de voz con la pantalla apagada, sin encenderla al reconocer `Bardo`;
- bloqueos de CPU y Wi-Fi mientras escucha;
- servicio de voz permanente con notificación;
- administración del dispositivo y modo quiosco preparados.

En el OPPO de desarrollo se ha aplicado además una configuración reversible:

- Bardo es la aplicación de inicio predeterminada;
- Bardo está excluido de la suspensión de batería de Android;
- el asistente de Google está desactivado para que no compita por el micrófono ni por el botón de asistente;
- el servicio de reconocimiento de voz de Android continúa disponible para Bardo.

Esto hace que Bardo sustituya **en la práctica** a `OK Google` en el teléfono dedicado. No convierte todavía a Bardo en un servicio de hotword integrado en el DSP del fabricante: la versión actual mantiene ciclos de `SpeechRecognizer`. La siguiente mejora de consumo y latencia será un detector local específico de la palabra `Bardo`.

La pantalla puede permanecer apagada. El servicio de primer plano reserva CPU y Wi-Fi y mantiene los ciclos del micrófono sin adquirir ningún bloqueo de pantalla. Al reconocer `Bardo`, abre una sesión nueva para la orden y la envía al PC sin iluminar el panel. Su canal de notificación no usa sonido, vibración ni luz. La fiabilidad absoluta después de reinicios o cierres forzosos del fabricante requiere el aprovisionamiento como propietario del dispositivo descrito a continuación.

## Bloqueo completo como dispositivo dedicado

Para que Android impida salir de Bardo, desactive la pantalla de bloqueo y otorgue a la aplicación las excepciones de un dispositivo corporativo dedicado, Bardo debe ser el **propietario del dispositivo** (`Device Owner`). Android solo permite asignarlo durante el aprovisionamiento inicial de un teléfono sin configurar.

> **Advertencia:** este paso exige restablecer el OPPO de fábrica y borra sus datos. No debe hacerse sin autorización expresa y una copia de seguridad verificada.

Después del restablecimiento, antes de añadir cuentas o completar la configuración normal:

1. Activa las opciones de desarrollador y la depuración USB.
2. Instala el APK de Bardo.
3. Ejecuta:

```powershell
adb shell dpm set-device-owner com.treska23.bardo/com.treska23.bardo.BardoDeviceAdminReceiver
```

4. Abre Bardo. La aplicación registra su actividad como inicio persistente, desactiva la pantalla de bloqueo y entra en modo quiosco automáticamente.

Para mantenimiento, conviene conservar acceso ADB autorizado desde el PC antes de activar el quiosco.

## Estado técnico

La versión actual utiliza `SpeechRecognizer` de Android con preferencia por reconocimiento offline. Ya valida el recorrido teléfono -> relay -> agente -> ControlPCIA -> Windows y la transición `Bardo` -> orden.

Las siguientes mejoras previstas son el detector de wake word totalmente local, respuesta hablada y estado del PC en la pantalla del teléfono.
