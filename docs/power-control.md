# Encender y apagar el ordenador por voz

## Bardo Android

Bardo puede controlar el encendido y el apagado del PC sin depender de Alexa.

### Apagado

La orden:

```text
Bardo
apaga el ordenador
```

se normaliza a `apaga ordenador`, que AlexaPc.Agent ejecuta como `system.shutdown`.
Windows programa el apagado cinco segundos después para que la respuesta pueda
volver al teléfono antes de que desaparezca la red.

Antes del primer apagado, si Bardo todavía no conoce la MAC del PC, consulta al
Agent mediante el comando interno `mac ordenador`. El Agent selecciona el adaptador
Ethernet físico preferente, devuelve su MAC y Bardo la guarda en sus preferencias.
Si no puede aprenderla, Bardo no apaga el PC para evitar dejar al usuario sin una
forma preparada de volver a encenderlo.

### Encendido

La orden:

```text
Bardo
enciende el ordenador
```

no usa el Relay ni el Agent, porque ambos pueden estar apagados. El OPPO envía
directamente un paquete mágico Wake-on-LAN por la red local a la MAC almacenada.
Se envían varias copias por broadcast UDP en los puertos 9 y 7.

La MAC queda visible/editable en la aplicación Android bajo:

```text
MAC del PC · Wake-on-LAN
```

Normalmente no es necesario escribirla a mano: se aprende automáticamente durante
el primer apagado mientras el PC sigue encendido.

El adaptador de red y la UEFI de Windows deben seguir configurados para aceptar
Wake-on-LAN desde apagado. En este equipo ya se había preparado el adaptador Realtek
para Magic Packet.

## Alexa

### Nombres que no colisionan

La aplicación y la Skill usan siempre **ordenador**. El interruptor físico no debe
llamarse `PC` ni `ordenador`, porque Alexa lo resolvería como dispositivo y cortaría
la corriente sin cerrar Windows.

En la app de Alexa, cambia el nombre del interruptor a algo que no dirías por
accidente, por ejemplo:

```text
corriente de la torre
```

### Apagado limpio

La orden que cierra Windows es:

```text
Alexa, dile a Bardo Control que apague el ordenador.
```

AlexaPc responde primero y programa el apagado cinco segundos después. Así el
resultado puede volver por WebSocket antes de que Windows cierre la red.

Para poder usar la frase corta `Alexa, apaga ordenador`, crea una rutina de voz en
la app de Alexa:

1. Evento de voz: `apaga ordenador`.
2. Acción personalizada: `dile a Bardo Control que apague el ordenador`.

La rutina no debe apagar directamente `corriente de la torre`.

### Encendido con el interruptor existente

La placa detectada es una MSI MAG X570S TOMAHAWK MAX WIFI. En su UEFI, activa:

```text
Settings > Advanced > Power Management Setup
Restore after AC Power Loss = Power On
```

Después crea la rutina `enciende ordenador` con estas acciones:

1. Apagar `corriente de la torre`.
2. Esperar cinco segundos.
3. Encender `corriente de la torre`.

El ciclo es necesario porque, tras un apagado limpio de Windows, el interruptor
sigue encendido. Al recuperar la corriente, la opción de la UEFI arranca el
ordenador. Usa esta rutina solo cuando el ordenador ya esté apagado: si se ejecuta
mientras está encendido, cortará la alimentación.
