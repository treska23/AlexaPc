using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPCIA;

internal sealed record AplicacionInstalada(
    string Nombre,
    string AppId);

/// <summary>
/// Obtiene por consola nombres reales de aplicaciones instaladas y entrega
/// al modelo únicamente las candidatas relacionadas con la petición. No abre
/// ni modifica nada y no contiene reglas específicas para una aplicación.
/// </summary>
internal static class InventarioAplicaciones
{
    private static readonly TimeSpan Vigencia =
        TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim Sincronizacion = new(1, 1);
    private static readonly HashSet<string> PalabrasVacias =
        new(StringComparer.Ordinal)
        {
            "abre", "abrir", "abreme", "inicia", "iniciar", "lanza",
            "lanzar", "ejecuta", "ejecutar", "pon", "poner", "el", "la",
            "los", "las", "un", "una", "unos", "unas", "de", "del", "al",
            "en", "mi", "por", "favor", "aplicacion", "aplicaciones",
            "programa", "programas", "ventana", "ventanas"
        };

    private static IReadOnlyList<AplicacionInstalada>? _cache;
    private static DateTimeOffset _cacheHasta;

    public static async Task<string> ObtenerContextoRelacionadoAsync(
        string peticion,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AplicacionInstalada> aplicaciones =
            await ObtenerAplicacionesAsync(cancellationToken);
        IReadOnlyList<AplicacionInstalada> candidatas =
            SeleccionarCandidatas(peticion, aplicaciones);

        if (candidatas.Count == 0)
        {
            return string.Empty;
        }

        return JsonSerializer.Serialize(
            candidatas.Select(aplicacion => new
            {
                nombre = aplicacion.Nombre,
                appId = aplicacion.AppId
            }));
    }

    internal static IReadOnlyList<AplicacionInstalada>
        SeleccionarCandidatas(
            string peticion,
            IReadOnlyList<AplicacionInstalada> aplicaciones)
    {
        string peticionNormalizada = Normalizar(peticion);
        string[] terminos = ExtraerTerminos(peticionNormalizada);

        if (terminos.Length == 0)
        {
            return [];
        }

        return aplicaciones
            .Where(aplicacion =>
                !EsDesinstalador(aplicacion.Nombre))
            .Select(aplicacion => new
            {
                Aplicacion = aplicacion,
                Puntuacion = Puntuar(
                    peticionNormalizada,
                    terminos,
                    aplicacion.Nombre)
            })
            .Where(candidata => candidata.Puntuacion > 0)
            .OrderByDescending(candidata => candidata.Puntuacion)
            .ThenBy(candidata => candidata.Aplicacion.Nombre)
            .Take(8)
            .Select(candidata => candidata.Aplicacion)
            .ToArray();
    }

    internal static void InvalidarCache()
    {
        _cache = null;
        _cacheHasta = default;
    }

    internal static async Task PrecalentarAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await ObtenerAplicacionesAsync(
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            // La primera petición puede volver a intentarlo y devolverá el
            // error real al móvil si Windows sigue sin ofrecer el inventario.
        }
    }

    internal static async Task<IReadOnlyList<AplicacionInstalada>>
        ObtenerAplicacionesAsync(
            CancellationToken cancellationToken)
    {
        if (_cache is not null
            && DateTimeOffset.UtcNow < _cacheHasta)
        {
            return _cache;
        }

        await Sincronizacion.WaitAsync(cancellationToken);

        try
        {
            if (_cache is not null
                && DateTimeOffset.UtcNow < _cacheHasta)
            {
                return _cache;
            }

            const string comando =
                "Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress";
            ResultadoEjecucionPowerShell resultado =
                await EjecutorPowerShell.EjecutarAsync(
                    comando,
                    cancellationToken);

            if (!resultado.Ejecutado
                || resultado.CodigoSalida != 0
                || string.IsNullOrWhiteSpace(resultado.Salida))
            {
                return [];
            }

            _cache = Deserializar(resultado.Salida);
            _cacheHasta = DateTimeOffset.UtcNow + Vigencia;
            return _cache;
        }
        finally
        {
            Sincronizacion.Release();
        }
    }

    private static IReadOnlyList<AplicacionInstalada> Deserializar(
        string json)
    {
        using JsonDocument documento = JsonDocument.Parse(json);
        IEnumerable<JsonElement> elementos =
            documento.RootElement.ValueKind == JsonValueKind.Array
                ? documento.RootElement.EnumerateArray()
                : [documento.RootElement];

        return elementos
            .Select(elemento => new AplicacionInstalada(
                Limpiar(
                    elemento.TryGetProperty(
                        "Name",
                        out JsonElement nombre)
                        ? nombre.GetString()
                        : null),
                Limpiar(
                    elemento.TryGetProperty(
                        "AppID",
                        out JsonElement appId)
                        ? appId.GetString()
                        : null)))
            .Where(aplicacion =>
                aplicacion.Nombre.Length > 0
                && aplicacion.AppId.Length > 0)
            .DistinctBy(aplicacion =>
                aplicacion.Nombre + "\n" + aplicacion.AppId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int Puntuar(
        string peticion,
        IReadOnlyList<string> terminos,
        string nombre)
    {
        string nombreNormalizado = Normalizar(nombre);
        string[] palabrasNombre = Regex
            .Matches(nombreNormalizado, @"[a-z0-9]+")
            .Select(coincidencia => coincidencia.Value)
            .ToArray();
        int puntuacion = 0;

        if (peticion.Contains(
                nombreNormalizado,
                StringComparison.Ordinal))
        {
            puntuacion += 1_000;
        }

        foreach (string termino in terminos)
        {
            int mejor = 0;

            foreach (string palabra in palabrasNombre)
            {
                if (palabra.Equals(
                        termino,
                        StringComparison.Ordinal))
                {
                    mejor = Math.Max(mejor, 120);
                    continue;
                }

                if (termino.Length >= 3
                    && palabra.Length >= 3
                    && (palabra.Contains(
                            termino,
                            StringComparison.Ordinal)
                        || termino.Contains(
                            palabra,
                            StringComparison.Ordinal)))
                {
                    mejor = Math.Max(mejor, 70);
                    continue;
                }

                int tolerancia = termino.Length >= 7
                    ? 2
                    : termino.Length >= 4
                        ? 1
                        : 0;

                if (tolerancia > 0
                    && DistanciaEdicion(termino, palabra)
                    <= tolerancia)
                {
                    mejor = Math.Max(mejor, 45);
                }
            }

            puntuacion += mejor;
        }

        return puntuacion;
    }

    private static string[] ExtraerTerminos(string peticion)
    {
        return Regex
            .Matches(peticion, @"[a-z0-9]+")
            .Select(coincidencia => coincidencia.Value)
            .Where(termino =>
                termino.Length >= 2
                && !PalabrasVacias.Contains(termino))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool EsDesinstalador(string nombre)
    {
        string normalizado = Normalizar(nombre);
        return Regex.IsMatch(
            normalizado,
            @"\b(?:desinstalar|desinstalador|uninstall|uninstaller|remove)\b",
            RegexOptions.CultureInvariant);
    }

    private static string Normalizar(string texto)
    {
        string descompuesto = (texto ?? string.Empty)
            .Normalize(NormalizationForm.FormD);
        var resultado = new StringBuilder(descompuesto.Length);

        foreach (char caracter in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter)
                != UnicodeCategory.NonSpacingMark)
            {
                resultado.Append(
                    char.ToLowerInvariant(caracter));
            }
        }

        return resultado
            .ToString()
            .Normalize(NormalizationForm.FormC);
    }

    private static string Limpiar(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return string.Empty;
        }

        string limpio = new(
            valor
                .Where(caracter => !char.IsControl(caracter))
                .Take(500)
                .ToArray());
        return limpio.Trim();
    }

    private static int DistanciaEdicion(
        string izquierda,
        string derecha)
    {
        int[] anterior = Enumerable.Range(
                0,
                derecha.Length + 1)
            .ToArray();
        int[] actual = new int[derecha.Length + 1];

        for (int i = 1; i <= izquierda.Length; i++)
        {
            actual[0] = i;

            for (int j = 1; j <= derecha.Length; j++)
            {
                int coste = izquierda[i - 1] == derecha[j - 1]
                    ? 0
                    : 1;
                actual[j] = Math.Min(
                    Math.Min(
                        actual[j - 1] + 1,
                        anterior[j] + 1),
                    anterior[j - 1] + coste);
            }

            (anterior, actual) = (actual, anterior);
        }

        return anterior[derecha.Length];
    }
}
