using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlPCIA;

internal sealed record EstadoOllama(
    bool Disponible,
    string Modelo,
    string Mensaje);

internal sealed record MensajeOllama(
    [property: JsonPropertyName("role")] string Rol,
    [property: JsonPropertyName("content")] string Contenido,
    [property: JsonPropertyName("tool_calls")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LlamadaHerramientaOllama>? LlamadasHerramientas = null,
    [property: JsonPropertyName("tool_name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NombreHerramienta = null);

internal sealed record LlamadaHerramientaOllama(
    [property: JsonPropertyName("type")] string Tipo,
    [property: JsonPropertyName("function")]
    FuncionLlamadaOllama Funcion);

internal sealed record FuncionLlamadaOllama(
    [property: JsonPropertyName("name")] string Nombre,
    [property: JsonPropertyName("arguments")]
    IReadOnlyDictionary<string, JsonElement> Argumentos);

internal sealed record PropiedadHerramientaOllama(
    [property: JsonPropertyName("type")] string Tipo,
    [property: JsonPropertyName("description")] string Descripcion);

internal sealed record ParametrosHerramientaOllama(
    [property: JsonPropertyName("type")] string Tipo,
    [property: JsonPropertyName("properties")]
    IReadOnlyDictionary<string, PropiedadHerramientaOllama> Propiedades,
    [property: JsonPropertyName("required")]
    IReadOnlyList<string> Requeridos);

internal sealed record FuncionHerramientaOllama(
    [property: JsonPropertyName("name")] string Nombre,
    [property: JsonPropertyName("description")] string Descripcion,
    [property: JsonPropertyName("parameters")]
    ParametrosHerramientaOllama Parametros);

internal sealed record HerramientaOllama(
    [property: JsonPropertyName("type")] string Tipo,
    [property: JsonPropertyName("function")]
    FuncionHerramientaOllama Funcion);

internal sealed record RespuestaChatOllama(
    [property: JsonPropertyName("message")] MensajeOllama Mensaje);

internal static class ClienteOllama
{
    private static readonly Uri DireccionBase =
        ObtenerDireccionBase();

    private static readonly HttpClient Cliente = new()
    {
        BaseAddress = DireccionBase,
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static string Modelo { get; } =
        ObtenerModelo();

    public static Uri Direccion => DireccionBase;

    public static async Task<EstadoOllama> DiagnosticarAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage respuesta =
                await Cliente.GetAsync(
                    "api/tags",
                    cancellationToken);

            respuesta.EnsureSuccessStatusCode();

            await using Stream contenido =
                await respuesta.Content.ReadAsStreamAsync(
                    cancellationToken);

            using JsonDocument json =
                await JsonDocument.ParseAsync(
                    contenido,
                    cancellationToken: cancellationToken);

            bool modeloInstalado =
                json.RootElement
                    .GetProperty("models")
                    .EnumerateArray()
                    .Any(modelo =>
                        string.Equals(
                            modelo.GetProperty("name").GetString(),
                            Modelo,
                            StringComparison.OrdinalIgnoreCase)
                        ||
                        string.Equals(
                            modelo.GetProperty("model").GetString(),
                            Modelo,
                            StringComparison.OrdinalIgnoreCase));

            if (!modeloInstalado)
            {
                return new EstadoOllama(
                    false,
                    Modelo,
                    $"Ollama responde, pero el modelo {Modelo} no está instalado.");
            }

            return new EstadoOllama(
                true,
                Modelo,
                $"Ollama y el modelo {Modelo} están disponibles.");
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException)
        {
            return new EstadoOllama(
                false,
                Modelo,
                "No se pudo conectar con Ollama en " +
                $"{DireccionBase}: {ex.Message}");
        }
    }

    public static async Task<MensajeOllama> ConversarAsync(
        IReadOnlyList<MensajeOllama> mensajes,
        IReadOnlyList<HerramientaOllama> herramientas,
        CancellationToken cancellationToken = default)
    {
        var peticion = new
        {
            model = Modelo,
            messages = mensajes,
            tools = herramientas,
            stream = false,
            think = ObtenerRazonamiento(),
            keep_alive =
                Environment.GetEnvironmentVariable(
                    "CONTROLPCIA_OLLAMA_KEEP_ALIVE")
                ?? "30m",
            options = new
            {
                temperature = 0
            }
        };

        using HttpResponseMessage respuesta =
            await Cliente.PostAsJsonAsync(
                "api/chat",
                peticion,
                cancellationToken);

        respuesta.EnsureSuccessStatusCode();

        await using Stream contenido =
            await respuesta.Content.ReadAsStreamAsync(
                cancellationToken);

        RespuestaChatOllama? json =
            await JsonSerializer.DeserializeAsync<RespuestaChatOllama>(
                contenido,
                cancellationToken: cancellationToken);

        if (json?.Mensaje is null)
        {
            throw new JsonException(
                "Ollama no devolvió un mensaje válido.");
        }

        return json.Mensaje with
        {
            Contenido = json.Mensaje.Contenido ?? string.Empty
        };
    }

    private static Uri ObtenerDireccionBase()
    {
        string valor =
            Environment.GetEnvironmentVariable(
                "CONTROLPCIA_OLLAMA_URL")
            ?? "http://127.0.0.1:11434/";

        if (!Uri.TryCreate(valor, UriKind.Absolute, out Uri? direccion)
            ||
            direccion.Scheme != Uri.UriSchemeHttp
            ||
            !direccion.IsLoopback)
        {
            throw new InvalidOperationException(
                "CONTROLPCIA_OLLAMA_URL debe ser una dirección HTTP local.");
        }

        string normalizada =
            direccion.AbsoluteUri.EndsWith('/')
                ? direccion.AbsoluteUri
                : direccion.AbsoluteUri + "/";

        return new Uri(normalizada);
    }

    private static string ObtenerModelo()
    {
        string modelo =
            Environment.GetEnvironmentVariable(
                "CONTROLPCIA_OLLAMA_MODELO")
            ?? "qwen3.5:9b";

        modelo = modelo.Trim();

        if (modelo.Length is < 1 or > 120
            ||
            modelo.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "CONTROLPCIA_OLLAMA_MODELO no es válido.");
        }

        return modelo;
    }

    private static object ObtenerRazonamiento()
    {
        string? configurado =
            Environment.GetEnvironmentVariable(
                "CONTROLPCIA_OLLAMA_RAZONAMIENTO");

        if (!string.IsNullOrWhiteSpace(configurado))
        {
            string valor = configurado.Trim();

            if (bool.TryParse(
                    valor,
                    out bool booleano))
            {
                return booleano;
            }

            if (valor.Equals(
                    "low",
                    StringComparison.OrdinalIgnoreCase)
                ||
                valor.Equals(
                    "medium",
                    StringComparison.OrdinalIgnoreCase)
                ||
                valor.Equals(
                    "high",
                    StringComparison.OrdinalIgnoreCase))
            {
                return valor.ToLowerInvariant();
            }

            throw new InvalidOperationException(
                "CONTROLPCIA_OLLAMA_RAZONAMIENTO debe ser true, false, low, medium o high.");
        }

        return Modelo.StartsWith(
            "gpt-oss",
            StringComparison.OrdinalIgnoreCase)
                ? "low"
                : false;
    }
}
