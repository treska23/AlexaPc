# Encender y apagar el ordenador con Alexa

## Nombres que no colisionan

La aplicación y la Skill usan siempre **ordenador**. El interruptor físico no debe
llamarse `PC` ni `ordenador`, porque Alexa lo resolvería como dispositivo y cortaría
la corriente sin cerrar Windows.

En la app de Alexa, cambia el nombre del interruptor a algo que no dirías por
accidente, por ejemplo:

```text
corriente de la torre
```

## Apagado limpio

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

## Encendido con el interruptor existente

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

## Alternativa futura: Wake-on-LAN

El adaptador Realtek aparece habilitado como dispositivo de reactivación en
Windows. Wake-on-LAN evita cortar corriente, pero necesita otro dispositivo que
permanezca encendido en la red local para enviar el paquete mágico (router, NAS,
Raspberry Pi u otro equipo). El propio AlexaPc no puede despertarse a sí mismo una
vez apagado y Cloudflare Workers no tiene acceso directo a la red doméstica.
