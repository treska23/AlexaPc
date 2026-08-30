# Bardo: asistente local

AlexaPc puede usar un modelo local como cerebro para interpretar lenguaje natural sin usar ninguna API de pago.

## Flujo

```text
Alexa
  -> Bardo Control
  -> relay
  -> AlexaPc.Agent
  -> comando exacto? -> ejecutar inmediatamente
  -> texto libre? -> Llama local
                  -> respuesta de voz
                  -> o selección de herramientas autorizadas
```

Llama nunca ejecuta código arbitrario. Solo puede seleccionar nombres ya existentes en `%LOCALAPPDATA%\AlexaPc\commands.json`.

## Servidores locales detectados

Con `provider: "auto"`, AlexaPc intenta en este orden:

1. Ollama: `http://127.0.0.1:11434`
2. Servidor OpenAI-compatible de LM Studio: `http://127.0.0.1:1234`
3. Servidor OpenAI-compatible de llama.cpp: `http://127.0.0.1:8080`

No se usa OpenAI ni ningún servicio remoto. `OpenAI-compatible` describe únicamente el formato HTTP que exponen algunos servidores locales.

Si no se especifica modelo, AlexaPc prefiere uno cuyo nombre contenga `llama`, después `qwen`, y finalmente el primer modelo cargado/disponible.

## Configuración

La primera vez que se inicia AlexaPc se crea:

```text
%LOCALAPPDATA%\AlexaPc\assistant.json
```

Configuración automática por defecto:

```json
{
  "enabled": true,
  "provider": "auto",
  "baseUrl": null,
  "model": null,
  "timeoutSeconds": 5
}
```

Para fijar Ollama y un modelo concreto:

```json
{
  "enabled": true,
  "provider": "ollama",
  "baseUrl": "http://127.0.0.1:11434",
  "model": "llama3.1:8b",
  "timeoutSeconds": 5
}
```

Para LM Studio o llama.cpp:

```json
{
  "enabled": true,
  "provider": "openai-compatible",
  "baseUrl": "http://127.0.0.1:1234",
  "model": null,
  "timeoutSeconds": 5
}
```

## Qué hace la primera versión

- Los comandos exactos siguen funcionando sin pasar por Llama.
- Una frase no reconocida se envía al modelo local.
- Llama puede seleccionar hasta cuatro comandos autorizados para una petición compuesta.
- Llama puede contestar preguntas breves; la respuesta vuelve por el relay y Alexa la lee.
- Las acciones de apagar, reiniciar, suspender y bloquear requieren que la frase del usuario las pida explícitamente.
- Si Llama inventa una herramienta que no existe en `commands.json`, AlexaPc la rechaza.

Ejemplos esperados:

```text
quiero ver YouTube
se oye demasiado alto
abre YouTube y baja el volumen
necesito que pauses lo que estoy viendo
dime qué es un agujero negro
```

## Restricción de latencia

Alexa espera respuestas muy rápidas. Los límites están anidados para que cada capa pueda devolver un error útil antes de que expire la siguiente:

```text
inferencia y cola local:  máximo 5.000 ms
Cloudflare Worker:        máximo 6.200 ms
Lambda de Alexa:          máximo 7.200 ms
presupuesto de Alexa:     menos de 8.000 ms
```

El agente precarga el modelo de Ollama al iniciar, conserva en caché el backend resuelto y pide a Ollama que mantenga el modelo cargado durante 24 horas. En modelos con razonamiento, como Qwen, se usa `think: false`: Bardo necesita una decisión JSON breve, no una cadena de razonamiento que consuma el presupuesto sin producir respuesta.

Si una carga fría supera los 5 segundos, el agente devuelve un error controlado al relay y lanza una única precarga de recuperación en segundo plano. Los comandos exactos no pasan por Llama y siguen disponibles durante esa precarga.

Los eventos detallados se escriben en `%LOCALAPPDATA%\AlexaPc\logs`: recepción, ruta exacta o natural, detección/caché del backend, inferencia, herramienta, resultado y envío WebSocket.

## Pruebas

```powershell
dotnet build AlexaPc.sln --configuration Release
node --test skill/tests/alexa-skill.test.cjs
Push-Location cloudflare/worker
npm ci
npm test
npx wrangler deploy --dry-run --outdir dist
Pop-Location
```

Las pruebas del Worker levantan Workerd localmente y cubren autenticación, `/health`, orden exacta, órdenes concurrentes, respuesta natural y limpieza de una petición que agota su timeout.

## Próximas herramientas

La arquitectura está preparada para añadir herramientas locales más ricas sin dar acceso arbitrario al sistema, por ejemplo:

- abrir proyectos de Visual Studio;
- buscar archivos;
- consultar `git status`;
- abrir proyectos concretos de ChatGPT en el navegador;
- rutinas como `voy a dibujar` o `voy a tocar la batería`;
- memoria local y recordatorios;
- información del sistema y procesos.
