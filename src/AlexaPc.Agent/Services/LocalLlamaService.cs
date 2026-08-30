using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class LocalLlamaService
{
    private const int MinimumInferenceTimeoutSeconds = 2;
    private const int MaximumInferenceTimeoutSeconds = 5;
    private const string OllamaKeepAlive = "24h";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = new();
    private readonly AssistantConfigurationService _configurationService;
    private readonly AppLogService _log;
    private readonly SemaphoreSlim _backendGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly object _warmUpSync = new();
    private ResolvedBackend? _cachedBackend;
    private BackendCacheKey? _cachedBackendKey;
    private Task? _warmUpTask;

    public LocalLlamaService(
        AssistantConfigurationService configurationService,
        AppLogService log)
    {
        _configurationService = configurationService;
        _log = log;
    }

    public async Task<AssistantDecision> DecideAsync(
        string utterance,
        IReadOnlyList<CommandDefinition> commands,
        CancellationToken cancellationToken = default)
    {
        var settings = _configurationService.Load();
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("El asistente local está desactivado en assistant.json.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = Math.Clamp(
            settings.TimeoutSeconds,
            MinimumInferenceTimeoutSeconds,
            MaximumInferenceTimeoutSeconds);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        ResolvedBackend? backend = null;

        try
        {
            backend = await ResolveBackendAsync(settings, timeout.Token).ConfigureAwait(false);
            if (backend is null)
            {
                throw new InvalidOperationException(
                    "No encuentro un servidor local de Llama. Arranca Ollama, LM Studio o llama.cpp y vuelve a intentarlo.");
            }

            var systemPrompt = BuildSystemPrompt(commands);
            string responseText;

            await _inferenceGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                _log.Info("local_llama_inference_started", new
                {
                    provider = backend.Provider,
                    model = backend.Model,
                    timeoutSeconds
                });

                responseText = backend.Provider == "ollama"
                    ? await CallOllamaAsync(backend, systemPrompt, utterance, timeout.Token).ConfigureAwait(false)
                    : await CallOpenAiCompatibleAsync(backend, systemPrompt, utterance, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _inferenceGate.Release();
            }

            var decision = ParseDecision(responseText, commands);
            _log.Info("local_assistant_decision", new
            {
                provider = backend.Provider,
                model = backend.Model,
                kind = decision.Kind,
                commands = decision.Commands,
                durationMs = stopwatch.ElapsedMilliseconds
            });

            return decision;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Info("local_llama_inference_timed_out", new
            {
                provider = backend?.Provider,
                model = backend?.Model,
                timeoutSeconds,
                durationMs = stopwatch.ElapsedMilliseconds
            });
            _ = WarmUpAsync();
            throw new TimeoutException("Llama local ha tardado demasiado en responder.");
        }
        catch (HttpRequestException ex)
        {
            InvalidateBackend();
            _log.Error("local_llama_http_failed", ex, new
            {
                provider = backend?.Provider,
                model = backend?.Model,
                durationMs = stopwatch.ElapsedMilliseconds
            });
            throw;
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        lock (_warmUpSync)
        {
            if (_warmUpTask is { IsCompleted: false })
            {
                return _warmUpTask;
            }

            _warmUpTask = WarmUpCoreAsync(cancellationToken);
            return _warmUpTask;
        }
    }

    private async Task WarmUpCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _configurationService.Load();
        if (!settings.Enabled)
        {
            _log.Info("local_assistant_warmup_skipped", new { reason = "disabled" });
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _log.Info("local_assistant_warmup_started");

        try
        {
            var backend = await ResolveBackendAsync(settings, cancellationToken).ConfigureAwait(false);
            if (backend is null)
            {
                throw new InvalidOperationException("No hay ningún servidor local de Llama disponible.");
            }

            if (backend.Provider == "ollama")
            {
                await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await WarmUpOllamaAsync(backend, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _inferenceGate.Release();
                }
            }

            _log.Info("local_assistant_warmup_completed", new
            {
                provider = backend.Provider,
                model = backend.Model,
                durationMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (OperationCanceledException)
        {
            _log.Info("local_assistant_warmup_cancelled", new
            {
                durationMs = stopwatch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            InvalidateBackend();
            _log.Error("local_assistant_warmup_failed", ex, new
            {
                durationMs = stopwatch.ElapsedMilliseconds
            });
        }
    }

    private async Task<ResolvedBackend?> ResolveBackendAsync(
        AssistantSettings settings,
        CancellationToken cancellationToken)
    {
        var cacheKey = BackendCacheKey.From(settings);
        if (_cachedBackend is not null && _cachedBackendKey == cacheKey)
        {
            _log.Info("local_llama_backend_cache_hit", new
            {
                provider = _cachedBackend.Provider,
                model = _cachedBackend.Model
            });
            return _cachedBackend;
        }

        await _backendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedBackend is not null && _cachedBackendKey == cacheKey)
            {
                return _cachedBackend;
            }

            _log.Info("local_llama_backend_discovery_started", new
            {
                provider = cacheKey.Provider,
                configuredModel = !string.IsNullOrWhiteSpace(cacheKey.Model)
            });

            var candidates = BuildCandidates(settings, cacheKey.Provider);
            foreach (var candidate in candidates.Distinct())
            {
                var model = await TryResolveModelAsync(
                        candidate.Provider,
                        candidate.BaseUrl,
                        settings.Model,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(model))
                {
                    continue;
                }

                _cachedBackend = new ResolvedBackend(candidate.Provider, candidate.BaseUrl, model);
                _cachedBackendKey = cacheKey;
                _log.Info("local_llama_backend_resolved", new
                {
                    provider = candidate.Provider,
                    model
                });
                return _cachedBackend;
            }

            return null;
        }
        finally
        {
            _backendGate.Release();
        }
    }

    private static IReadOnlyList<(string Provider, string BaseUrl)> BuildCandidates(
        AssistantSettings settings,
        string provider)
    {
        var candidates = new List<(string Provider, string BaseUrl)>();

        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            var explicitBase = NormalizeBaseUrl(settings.BaseUrl);
            if (provider is "auto" or "ollama")
            {
                candidates.Add(("ollama", explicitBase));
            }

            if (provider is "auto" or "openai" or "openai-compatible")
            {
                candidates.Add(("openai-compatible", explicitBase));
            }
        }
        else
        {
            if (provider is "auto" or "ollama")
            {
                candidates.Add(("ollama", "http://127.0.0.1:11434"));
            }

            if (provider is "auto" or "openai" or "openai-compatible")
            {
                candidates.Add(("openai-compatible", "http://127.0.0.1:1234"));
                candidates.Add(("openai-compatible", "http://127.0.0.1:8080"));
            }
        }

        return candidates;
    }

    private async Task<string?> TryResolveModelAsync(
        string provider,
        string baseUrl,
        string? configuredModel,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1300));

        try
        {
            var url = provider == "ollama"
                ? $"{baseUrl}/api/tags"
                : $"{baseUrl}/v1/models";

            using var response = await _httpClient.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _log.Info("local_llama_backend_probe_completed", new
                {
                    provider,
                    success = false,
                    status = (int)response.StatusCode,
                    durationMs = stopwatch.ElapsedMilliseconds
                });
                return null;
            }

            if (!string.IsNullOrWhiteSpace(configuredModel))
            {
                _log.Info("local_llama_backend_probe_completed", new
                {
                    provider,
                    success = true,
                    configuredModel = true,
                    durationMs = stopwatch.ElapsedMilliseconds
                });
                return configuredModel.Trim();
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var modelNames = provider == "ollama"
                ? ReadOllamaModels(document.RootElement)
                : ReadOpenAiModels(document.RootElement);

            var model = ChooseModel(modelNames);
            _log.Info("local_llama_backend_probe_completed", new
            {
                provider,
                success = model is not null,
                configuredModel = false,
                durationMs = stopwatch.ElapsedMilliseconds
            });
            return model;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Info("local_llama_backend_probe_failed", new
            {
                provider,
                errorType = ex.GetType().Name,
                durationMs = stopwatch.ElapsedMilliseconds
            });
            return null;
        }
    }

    private async Task WarmUpOllamaAsync(
        ResolvedBackend backend,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = backend.Model,
            prompt = string.Empty,
            stream = false,
            keep_alive = OllamaKeepAlive,
            options = new { num_predict = 1 }
        };

        using var response = await PostJsonAsync(
                $"{backend.BaseUrl}/api/generate",
                payload,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama no pudo precargar el modelo (HTTP {(int)response.StatusCode}).");
        }
    }

    private async Task<string> CallOllamaAsync(
        ResolvedBackend backend,
        string systemPrompt,
        string utterance,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = backend.Model,
            stream = false,
            format = "json",
            think = false,
            keep_alive = OllamaKeepAlive,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = utterance }
            },
            options = new
            {
                temperature = 0.1,
                num_predict = 96
            }
        };

        using var response = await PostJsonAsync(
                $"{backend.BaseUrl}/api/chat",
                payload,
                cancellationToken)
            .ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama ha respondido con HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("Ollama no ha devuelto una respuesta válida.");
        }

        var result = content.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("Ollama ha devuelto contenido vacío.");
        }

        return result;
    }

    private async Task<string> CallOpenAiCompatibleAsync(
        ResolvedBackend backend,
        string systemPrompt,
        string utterance,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = backend.Model,
            temperature = 0.1,
            max_tokens = 96,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = utterance }
            }
        };

        using var response = await PostJsonAsync(
                $"{backend.BaseUrl}/v1/chat/completions",
                payload,
                cancellationToken)
            .ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"El servidor local ha respondido con HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("El servidor local no ha devuelto una respuesta válida.");
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("El servidor local no ha devuelto contenido.");
        }

        return content.GetString() ?? string.Empty;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(
        string url,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static AssistantDecision ParseDecision(
        string raw,
        IReadOnlyList<CommandDefinition> availableCommands)
    {
        var json = ExtractJsonObject(raw);
        var modelDecision = JsonSerializer.Deserialize<ModelDecision>(json, JsonOptions)
                            ?? throw new InvalidOperationException("Llama no ha devuelto una decisión válida.");

        var kind = (modelDecision.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind is not ("execute" or "reply" or "clarify"))
        {
            throw new InvalidOperationException("Llama ha devuelto un tipo de decisión desconocido.");
        }

        var commandLookup = availableCommands.ToDictionary(
            command => command.Name,
            command => command.Name,
            StringComparer.OrdinalIgnoreCase);

        var validated = new List<string>();
        foreach (var requested in modelDecision.Commands ?? [])
        {
            if (commandLookup.TryGetValue(requested.Trim(), out var canonical))
            {
                validated.Add(canonical);
            }
            else
            {
                throw new InvalidOperationException($"Llama ha intentado usar una herramienta no autorizada: {requested}");
            }
        }

        if (kind == "execute" && validated.Count == 0)
        {
            throw new InvalidOperationException("Llama no ha elegido ninguna herramienta para ejecutar.");
        }

        if (validated.Count > 4)
        {
            validated = validated.Take(4).ToList();
        }

        var reply = (modelDecision.Reply ?? string.Empty).Trim();
        if (kind != "execute" && string.IsNullOrWhiteSpace(reply))
        {
            reply = "No tengo suficiente información para responder a eso.";
        }

        return new AssistantDecision(kind, validated, reply);
    }

    private static string BuildSystemPrompt(IReadOnlyList<CommandDefinition> commands)
    {
        var catalog = string.Join(
            "\n",
            commands.Select(command => $"- {command.Name}: {command.Description}"));

        return $$"""
Eres Bardo, el cerebro local de un asistente de voz para Windows.
Tu trabajo es entender español natural y decidir si debes ejecutar herramientas autorizadas o responder brevemente.

Devuelve EXCLUSIVAMENTE un objeto JSON válido con esta forma:
{"kind":"execute|reply|clarify","commands":["nombre exacto"],"reply":"texto breve"}

Reglas:
- Para acciones del ordenador, usa kind="execute" y SOLO nombres exactos del catálogo.
- Puedes encadenar hasta 4 herramientas si el usuario pide varias cosas claramente.
- Nunca inventes comandos, rutas, programas, URLs, shell, PowerShell ni código.
- Para apagar, reiniciar, suspender o bloquear, solo ejecuta si el usuario lo pide de forma explícita.
- Para preguntas generales que puedas contestar con tu propio conocimiento, usa kind="reply".
- Si falta información o la acción no existe en el catálogo, usa kind="clarify".
- reply debe ser natural, en español, apto para ser leído en voz alta y como máximo dos frases cortas.

Herramientas autorizadas:
{{catalog}}
""";
    }

    private static string ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
            {
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("Llama no ha devuelto JSON.");
        }

        return trimmed[start..(end + 1)];
    }

    private static IReadOnlyList<string> ReadOllamaModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return models.EnumerateArray()
            .Select(model => model.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();
    }

    private static IReadOnlyList<string> ReadOpenAiModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return models.EnumerateArray()
            .Select(model => model.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();
    }

    private static string? ChooseModel(IReadOnlyList<string> models)
    {
        return models.FirstOrDefault(name => name.Contains("llama", StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault(name => name.Contains("qwen", StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault();
    }

    private static string NormalizeBaseUrl(string baseUrl) => baseUrl.Trim().TrimEnd('/');

    private void InvalidateBackend()
    {
        _cachedBackend = null;
        _cachedBackendKey = null;
    }

    private sealed record ResolvedBackend(string Provider, string BaseUrl, string Model);

    private sealed record BackendCacheKey(string Provider, string? BaseUrl, string? Model)
    {
        public static BackendCacheKey From(AssistantSettings settings) => new(
            (settings.Provider ?? "auto").Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(settings.BaseUrl) ? null : NormalizeBaseUrl(settings.BaseUrl),
            string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim());
    }

    private sealed class ModelDecision
    {
        public string? Kind { get; init; }
        public List<string>? Commands { get; init; }
        public string? Reply { get; init; }
    }
}

public sealed record AssistantDecision(string Kind, IReadOnlyList<string> Commands, string Reply);
