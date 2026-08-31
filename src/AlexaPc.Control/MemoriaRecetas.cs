using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ControlPCIA;

internal sealed record RecetaReferencia(
    string Intencion,
    IReadOnlyList<string> Comandos,
    int Exitos,
    double Similitud);

internal sealed class MemoriaRecetas
{
    private const int VersionActual = 2;
    private const int MaximoRecetas = 300;
    private const int MaximoComandosPorReceta = 10;
    private const int MaximoCaracteresComando = 4000;

    private static readonly HashSet<string> PalabrasVacias =
        new(StringComparer.Ordinal)
        {
            "a", "al", "con", "de", "del", "el", "en", "la", "las",
            "lo", "los", "mi", "para", "por", "que", "un", "una", "y",
            "abre", "abrir", "inicia", "iniciar", "lanza", "lanzar",
            "crea", "crear", "nuevo", "nueva", "haz", "hacer", "pon",
            "poner", "cierra", "cerrar", "dime", "muestra", "mostrar"
        };

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _ruta;
    private readonly SemaphoreSlim _puerta = new(1, 1);
    private List<RecetaPersistida>? _recetas;

    public static MemoriaRecetas Predeterminada { get; } =
        new(ObtenerRutaPredeterminada());

    internal MemoriaRecetas(string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta))
        {
            throw new ArgumentException(
                "La ruta de memoria no puede estar vacía.",
                nameof(ruta));
        }

        _ruta = Path.GetFullPath(ruta);
    }

    public async Task<IReadOnlyList<RecetaReferencia>> BuscarAsync(
        string instruccion,
        int maximo = 5,
        CancellationToken cancellationToken = default)
    {
        string normalizada = Normalizar(instruccion);

        if (normalizada.Length == 0 || maximo <= 0)
        {
            return [];
        }

        await _puerta.WaitAsync(cancellationToken);

        try
        {
            await CargarSiHaceFaltaAsync(cancellationToken);

            HashSet<string> tokens = ObtenerTokens(normalizada);

            return _recetas!
                .Select(receta => new
                {
                    Receta = receta,
                    Similitud = CalcularSimilitud(
                        normalizada,
                        tokens,
                        receta.Intencion)
                })
                .Where(candidata => candidata.Similitud >= 0.25)
                .Where(candidata =>
                    candidata.Receta.Comandos.Count > 0
                    &&
                    candidata.Receta.Comandos.All(comando =>
                        ValidadorPowerShell.Validar(comando).Permitido
                        && ControlWindows
                            .EsComandoCompatibleConModoConsola(comando))
                    && TieneVerificacionSuficiente(candidata.Receta))
                .OrderByDescending(candidata => candidata.Similitud)
                .ThenByDescending(candidata => candidata.Receta.Exitos)
                .ThenByDescending(candidata => candidata.Receta.UltimoExitoUtc)
                .Take(Math.Min(maximo, 10))
                .Select(candidata => new RecetaReferencia(
                    candidata.Receta.Intencion,
                    OptimizarComandosParaReutilizacion(
                        candidata.Receta.Comandos),
                    candidata.Receta.Exitos,
                    candidata.Similitud))
                .ToArray();
        }
        finally
        {
            _puerta.Release();
        }
    }

    public async Task<bool> AprenderAsync(
        string instruccion,
        IEnumerable<string> comandos,
        CancellationToken cancellationToken = default)
    {
        string normalizada = Normalizar(instruccion);

        string[] comandosValidos = OptimizarComandosParaReutilizacion(
            comandos
            .Select(comando => comando.Trim())
            .Where(comando => comando.Length is > 0 and <= MaximoCaracteresComando)
            .Where(comando =>
                ValidadorPowerShell.Validar(comando).Permitido
                && ControlWindows.EsComandoCompatibleConModoConsola(comando))
            .Take(MaximoComandosPorReceta)
            .ToArray());

        if (normalizada.Length == 0
            || comandosValidos.Length == 0
            || !TieneVerificacionSuficiente(comandosValidos))
        {
            return false;
        }

        await _puerta.WaitAsync(cancellationToken);

        try
        {
            await CargarSiHaceFaltaAsync(cancellationToken);

            List<RecetaPersistida> recetas = _recetas!;

            RecetaPersistida? existente =
                recetas.FirstOrDefault(receta =>
                    receta.Intencion.Equals(
                        normalizada,
                        StringComparison.Ordinal)
                    &&
                    receta.Comandos.SequenceEqual(
                        comandosValidos,
                        StringComparer.Ordinal));

            DateTimeOffset ahora = DateTimeOffset.UtcNow;

            if (existente is null)
            {
                // Una secuencia nueva que ya ha terminado correctamente corrige
                // las recetas anteriores de la misma intención. Así no se sigue
                // priorizando, por número de éxitos, una solución incompleta.
                recetas.RemoveAll(receta =>
                    receta.Intencion.Equals(
                        normalizada,
                        StringComparison.Ordinal));

                recetas.Add(
                    new RecetaPersistida
                    {
                        Intencion = normalizada,
                        Comandos = comandosValidos.ToList(),
                        Exitos = 1,
                        PrimerExitoUtc = ahora,
                        UltimoExitoUtc = ahora
                    });
            }
            else
            {
                existente.Exitos++;
                existente.UltimoExitoUtc = ahora;
            }

            _recetas = recetas
                .OrderByDescending(receta => receta.UltimoExitoUtc)
                .ThenByDescending(receta => receta.Exitos)
                .Take(MaximoRecetas)
                .ToList();

            await GuardarAsync(cancellationToken);
            return true;
        }
        finally
        {
            _puerta.Release();
        }
    }

    public async Task<int> ContarAsync(
        CancellationToken cancellationToken = default)
    {
        await _puerta.WaitAsync(cancellationToken);

        try
        {
            await CargarSiHaceFaltaAsync(cancellationToken);
            return _recetas!.Count;
        }
        finally
        {
            _puerta.Release();
        }
    }

    internal static string Normalizar(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        string descompuesto =
            texto.Trim().ToLowerInvariant().Normalize(
                NormalizationForm.FormD);

        var resultado = new StringBuilder(descompuesto.Length);
        bool ultimoFueEspacio = true;

        foreach (char caracter in descompuesto)
        {
            UnicodeCategory categoria =
                CharUnicodeInfo.GetUnicodeCategory(caracter);

            if (categoria == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(caracter))
            {
                resultado.Append(caracter);
                ultimoFueEspacio = false;
            }
            else if (!ultimoFueEspacio)
            {
                resultado.Append(' ');
                ultimoFueEspacio = true;
            }
        }

        return resultado.ToString().Trim();
    }

    private async Task CargarSiHaceFaltaAsync(
        CancellationToken cancellationToken)
    {
        if (_recetas is not null)
        {
            return;
        }

        if (!File.Exists(_ruta))
        {
            _recetas = [];
            return;
        }

        try
        {
            await using FileStream archivo = new(
                _ruta,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            AlmacenRecetas? almacen =
                await JsonSerializer.DeserializeAsync<AlmacenRecetas>(
                    archivo,
                    OpcionesJson,
                    cancellationToken);

            _recetas = almacen?.Version == VersionActual
                ? almacen.Recetas
                    .Where(EsRecetaEstructuralmenteValida)
                    .Take(MaximoRecetas)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            _recetas = [];
        }
    }

    private async Task GuardarAsync(CancellationToken cancellationToken)
    {
        string? carpeta = Path.GetDirectoryName(_ruta);

        if (string.IsNullOrWhiteSpace(carpeta))
        {
            throw new InvalidOperationException(
                "La memoria de aprendizaje no tiene una carpeta válida.");
        }

        Directory.CreateDirectory(carpeta);

        string temporal =
            Path.Combine(
                carpeta,
                $".{Path.GetFileName(_ruta)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream archivo = new(
                             temporal,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    archivo,
                    new AlmacenRecetas
                    {
                        Version = VersionActual,
                        Recetas = _recetas!
                    },
                    OpcionesJson,
                    cancellationToken);

                await archivo.FlushAsync(cancellationToken);
            }

            File.Move(temporal, _ruta, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporal))
            {
                File.Delete(temporal);
            }
        }
    }

    private static bool EsRecetaEstructuralmenteValida(
        RecetaPersistida receta)
    {
        return receta.Intencion.Length is > 0 and <= 1000
               &&
               receta.Comandos.Count is > 0 and <= MaximoComandosPorReceta
               &&
               receta.Comandos.All(comando =>
                   comando.Length is > 0 and <= MaximoCaracteresComando)
               &&
                receta.Exitos > 0;
    }

    private static bool TieneVerificacionSuficiente(
        RecetaPersistida receta)
    {
        return TieneVerificacionSuficiente(receta.Comandos);
    }

    private static bool TieneVerificacionSuficiente(
        IReadOnlyList<string> comandos)
    {
        ResultadoPasoControl[] pasos = comandos
            .Select((comando, indice) =>
                new ResultadoPasoControl(
                    indice + 1,
                    comando,
                    true,
                    0,
                    "resultado_correcto_almacenado",
                    string.Empty))
            .ToArray();

        return !ControlWindows.RequiereVerificacionTrasCambio(pasos);
    }

    private static string[] OptimizarComandosParaReutilizacion(
        IEnumerable<string> comandos)
    {
        string[] lista = comandos.ToArray();
        bool contieneAppIdVerificado = lista.Any(comando =>
            comando.Contains(
                "shell:AppsFolder\\",
                StringComparison.OrdinalIgnoreCase));
        bool contienePlantillaLiteral = lista.Any(comando =>
            comando.Contains(
                "Copy-Item",
                StringComparison.OrdinalIgnoreCase));

        return lista
            .Where(comando =>
                !(contieneAppIdVerificado
                  && comando.Contains(
                      "Get-StartApps",
                      StringComparison.OrdinalIgnoreCase)))
            .Where(comando =>
                !(contienePlantillaLiteral
                  && comando.Contains(
                      "Get-ChildItem",
                      StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static double CalcularSimilitud(
        string instruccion,
        HashSet<string> tokensInstruccion,
        string intencionReceta)
    {
        if (instruccion.Equals(intencionReceta, StringComparison.Ordinal))
        {
            return 1;
        }

        HashSet<string> tokensReceta =
            ObtenerTokens(intencionReceta);

        if (tokensInstruccion.Count == 0 || tokensReceta.Count == 0)
        {
            return 0;
        }

        int interseccion = tokensInstruccion.Intersect(tokensReceta).Count();
        int union = tokensInstruccion.Union(tokensReceta).Count();
        int menor = Math.Min(
            tokensInstruccion.Count,
            tokensReceta.Count);
        double jaccard =
            union == 0 ? 0 : (double)interseccion / union;
        double coberturaEntidad =
            menor == 0 ? 0 : (double)interseccion / menor;

        return Math.Max(
            jaccard,
            coberturaEntidad * 0.6);
    }

    private static HashSet<string> ObtenerTokens(string texto)
    {
        return texto
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !PalabrasVacias.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ObtenerRutaPredeterminada()
    {
        string carpeta = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(carpeta))
        {
            throw new InvalidOperationException(
                "Windows no devolvió la carpeta local de datos de la aplicación.");
        }

        return Path.Combine(
            carpeta,
            "ControlPCIA",
            "recetas-v2.json");
    }

    private sealed class AlmacenRecetas
    {
        public int Version { get; set; } = VersionActual;
        public List<RecetaPersistida> Recetas { get; set; } = [];
    }

    private sealed class RecetaPersistida
    {
        public string Intencion { get; set; } = string.Empty;
        public List<string> Comandos { get; set; } = [];
        public int Exitos { get; set; }
        public DateTimeOffset PrimerExitoUtc { get; set; }
        public DateTimeOffset UltimoExitoUtc { get; set; }
    }
}
