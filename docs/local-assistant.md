# Bardo: controlador local unificado

AlexaPc incorpora el núcleo estable de ControlPCIA como biblioteca. Alexa, el relay, la interfaz WPF y el control general de Windows viven ahora en el mismo repositorio y en el mismo proceso; el ejecutable independiente de ControlPCIA ya no es necesario para las órdenes de voz.

## Flujo

```text
Alexa
  -> Bardo Control
  -> relay
  -> AlexaPc.Agent
  -> comando exacto? -> ejecución inmediata
  -> orden de energía? -> catálogo protegido
  -> texto libre? -> AlexaPc.Control
                  -> control determinista
                  -> traducción local con Qwen si hace falta
                  -> respuesta breve por Alexa
```

La Skill separa los verbos de acción en intenciones propias y vuelve a unirlos al texto reconocido. Así, `abre Chrome` llega al agente como `abre Chrome`, no como solo `Chrome`.

## Qué puede controlar

- Abrir y cerrar aplicaciones o direcciones web.
- Buscar contenido en la web.
- Maximizar, minimizar, restaurar, colocar y traer ventanas al frente.
- Duplicar, extender y girar pantallas.
- Reproducir, pausar, avanzar, retroceder y cambiar el volumen.
- Consultar aplicaciones y ventanas abiertas.
- Ejecutar varias acciones relacionadas en una sola frase.

Las rutas conocidas son deterministas y no necesitan un modelo. Cuando no hay una traducción conocida, ControlPCIA consulta Ollama localmente y valida el plan antes de ejecutarlo.

## Ollama

El traductor usa por defecto:

```text
http://127.0.0.1:11434
qwen3.5:9b
```

Se puede cambiar con estas variables de entorno:

```text
CONTROLPCIA_OLLAMA_URL
CONTROLPCIA_OLLAMA_MODELO
CONTROLPCIA_OLLAMA_RAZONAMIENTO
```

El agente precalienta al iniciar tanto el inventario de aplicaciones como el traductor local. La mayoría de órdenes habituales no pasan por Qwen y responden de forma inmediata.

El archivo `%LOCALAPPDATA%\AlexaPc\assistant.json` se conserva para el asistente anterior y para el tratamiento protegido de variantes de órdenes de energía. No configura el motor general integrado.

## Seguridad

- Las órdenes conocidas usan controladores específicos de aplicaciones, ventanas, pantallas, multimedia y web.
- Los planes traducidos localmente pasan por validación antes de ejecutar PowerShell.
- Se rechazan eliminaciones, movimientos o cortes de archivos, desinstalaciones y operaciones de formato o reinicialización de discos.
- Apagar, reiniciar, suspender y bloquear quedan fuera del controlador general y requieren que el usuario lo pida explícitamente.

## Latencia

Alexa exige una respuesta antes de unos ocho segundos. Los límites siguen anidados:

```text
control local:       máximo 4.800 ms
Cloudflare Worker:   máximo 6.200 ms
Lambda de Alexa:     máximo 7.200 ms
presupuesto Alexa:   menos de 8.000 ms
```

Los eventos detallados se escriben en `%LOCALAPPDATA%\AlexaPc\logs`, incluidos la orden recibida, la ruta elegida, el estado del controlador y el tiempo empleado.

## Ejemplos

```text
abre Chrome
cierra el bloc de notas
busca vídeos de jazz en YouTube
maximiza Spotify y baja el volumen
coloca Chrome en la pantalla de la derecha
duplica las pantallas
qué programas tengo abiertos
```

## Pruebas

```powershell
dotnet restore AlexaPc.sln
dotnet build AlexaPc.sln --configuration Release --no-restore
dotnet test tests/AlexaPc.Control.Tests/AlexaPc.Control.Tests.csproj --configuration Release --no-build
node --test skill/tests/alexa-skill.test.cjs
Push-Location cloudflare/worker
npm test
Pop-Location
```
