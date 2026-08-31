# Invocación de una sola frase en Alexa

## Diagnóstico

La frase de una sola interacción sí está soportada por las Custom Skills en
español. Amazon documenta esta estructura:

```text
Dile a <nombre de invocación> que <acción>
```

Por tanto, con este modelo la frase principal es:

```text
Alexa, dile a Bardo Control que abra YouTube.
```

Alexa aporta `dile a`, el nombre de invocación y el conector `que`. La parte que
debe resolver el modelo de la Skill es `abra YouTube`, que coincide con la muestra
`abra {target}` de `OpenComputerIntent`. Por ese motivo no se incluye `que abra {target}`: obligaría a
entrenar una duplicación artificial del conector.

El fallo anterior ocurría antes de Lambda. La prueba es que no había respuesta ni
registro de petición mientras el flujo en dos turnos sí llegaba al ordenador. Sin el
historial de voz de Alexa no se puede demostrar qué transcripción concreta produjo
el dispositivo, pero sí hay una anomalía objetiva en el modelo: `bardo` incumple la
regla general de Amazon que prohíbe nombres de invocación de una sola palabra salvo
marcas o propiedad intelectual acreditadas. Amazon recomienda añadir otra palabra
cuando hay posibles solapamientos. `bardo control` cumple la regla de dos palabras,
es fonéticamente más distintivo y reduce la ambigüedad de enrutamiento.

Amazon no publica un registro que permita garantizar por búsqueda que un nombre no
colisiona. La comprobación definitiva se hace construyendo el modelo, probando por
voz varias veces y revisando en la app de Alexa cómo se transcribió la petición.

## Frases que se deben probar

En este orden:

```text
Alexa, dile a Bardo Control que abra YouTube.
Alexa, abre Bardo Control y abre YouTube.
Alexa, abre Bardo Control para abrir YouTube.
```

Las dos primeras siguen literalmente las formas documentadas por Amazon. La
tercera combina `Abre <nombre> para <acción>` con la muestra `abrir {target}`.

`Alexa, Bardo Control` abre la Skill, pero no incluye una intención. La forma
`Alexa, Bardo abre YouTube` no es una invocación directa documentada para `es-ES`.
La interacción sin nombre de Skill está limitada a `en-US`, por lo que no es una
base estable para este proyecto.

## Por qué se conserva AMAZON.SearchQuery

`AMAZON.SearchQuery` está disponible en español de España y está pensado para texto
poco predecible. AlexaPc puede recibir nombres de aplicaciones, búsquedas y órdenes
que no caben en una lista cerrada, así que los slots abiertos conservan esa
extensibilidad. Amazon no incluye la frase portadora en el valor del slot: por eso
cada familia de verbos tiene su propio intent y Lambda vuelve a unir ambos elementos.
Por ejemplo, `OpenComputerIntent` con el slot `Chrome` produce `abre Chrome`.

Amazon exige además que cada muestra con `AMAZON.SearchQuery` tenga una frase
portadora; todas las muestras la tienen, por ejemplo `abre {target}`, `busca
{query}` o `maximiza {target}`.

Cambiar a un slot personalizado podría mejorar el reconocimiento de una lista fija,
pero no solucionaría una petición que no llega a invocar la Skill. También obligaría
a mantener el modelo sincronizado con cada cambio local de `commands.json`.

## Cómo distinguir enrutamiento de ejecución

El código de Lambda registra una sola línea JSON segura por solicitud. Ejemplos:

```json
{"component":"AlexaPc.Skill","event":"request","requestType":"LaunchRequest","requestId":"...","locale":"es-ES","intentName":null,"command":null}
{"component":"AlexaPc.Skill","event":"request","requestType":"IntentRequest","requestId":"...","locale":"es-ES","intentName":"OpenComputerIntent","command":"abre YouTube"}
```

No se registran la API key, la URL del relay ni el identificador del dispositivo.

- Si no aparece ninguna línea, Alexa no ha enrutado la frase a la Skill.
- Si aparece `LaunchRequest`, Alexa abrió la Skill sin resolver la acción.
- Si aparece `IntentRequest` con un intent de acción y el verbo reconstruido, la invocación one-shot ya
  está resuelta y cualquier fallo posterior pertenece a Lambda, relay u ordenador.

## Fuentes oficiales consultadas

- [Understand How Users Invoke Custom Skills](https://developer.amazon.com/en-US/docs/alexa/custom-skills/understanding-how-users-invoke-custom-skills.html)
- [Choose the Invocation Name for a Custom Skill](https://developer.amazon.com/es-ES/docs/alexa/custom-skills/choose-the-invocation-name-for-a-custom-skill.html)
- [Slot Type Reference: AMAZON.SearchQuery](https://developer.amazon.com/en-US/docs/alexa/custom-skills/slot-type-reference.html#amazonsearchquery)
- [Functional Testing for a Custom Skill](https://developer.amazon.com/en-US/docs/alexa/custom-skills/functional-testing-for-a-custom-skill.html)
- [Steps to Avoid Overlaps When Choosing an Invocation Name](https://developer.amazon.com/en-US/blogs/alexa/alexa-skills-kit/2021/11/avoid-overlaps-when-choosing-an-invocation-name-for-your-alexa-skill)
