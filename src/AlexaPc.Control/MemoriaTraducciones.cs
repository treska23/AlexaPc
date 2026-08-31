using System.Text.Json;

namespace ControlPCIA;

internal sealed record TraduccionAprendida(
    string Intencion,
    string Comando,
    bool Consulta,
    int Exitos,
    double Similitud);

/// <summary>
/// Memoria sencilla para traducciones que ya terminaron con código de salida
/// correcto. Una coincidencia exacta evita volver a consultar al modelo; las
/// coincidencias aproximadas sólo se entregan como referencias.
/// </summary>
internal sealed class MemoriaTraducciones
{
    private const int MaximoEntradas = 300;
    private readonly string _ruta;
    private readonly SemaphoreSlim _puerta = new(1, 1);
    private List<Entrada>? _entradas;

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static MemoriaTraducciones Predeterminada { get; } =
        new(ObtenerRutaPredeterminada());

    internal MemoriaTraducciones(string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta))
        {
            throw new ArgumentException(
                "La ruta de memoria no puede estar vacía.",
                nameof(ruta));
        }

        _ruta = Path.GetFullPath(ruta);
    }

    public async Task<IReadOnlyList<TraduccionAprendida>>
        BuscarAsync(
            string instruccion,
            int maximo = 5,
            CancellationToken cancellationToken = default)
    {
        string intencion =
            MemoriaRecetas.Normalizar(instruccion);

        if (intencion.Length == 0 || maximo <= 0)
        {
            return [];
        }

        await _puerta.WaitAsync(cancellationToken);

        try
        {
            await CargarAsync(cancellationToken);
            HashSet<string> tokens =
                Tokens(intencion);

            return _entradas!
                .Select(entrada => new
                {
                    Entrada = entrada,
                    Similitud = Similitud(
                        intencion,
                        tokens,
                        entrada.Intencion)
                })
                .Where(candidata =>
                    candidata.Similitud >= 0.3
                    && EsComandoValido(
                        candidata.Entrada.Comando))
                .OrderByDescending(candidata =>
                    candidata.Similitud)
                .ThenByDescending(candidata =>
                    candidata.Entrada.Exitos)
                .Take(Math.Min(maximo, 10))
                .Select(candidata =>
                    new TraduccionAprendida(
                        candidata.Entrada.Intencion,
                        candidata.Entrada.Comando,
                        candidata.Entrada.Consulta,
                        candidata.Entrada.Exitos,
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
        string comando,
        bool consulta,
        CancellationToken cancellationToken = default)
    {
        string intencion =
            MemoriaRecetas.Normalizar(instruccion);
        comando = (comando ?? string.Empty).Trim();

        if (intencion.Length == 0
            || comando.Length is 0 or > 20_000
            || !EsComandoValido(comando))
        {
            return false;
        }

        await _puerta.WaitAsync(cancellationToken);

        try
        {
            await CargarAsync(cancellationToken);
            List<Entrada> entradas =
                _entradas!;
            Entrada? existente =
                entradas.FirstOrDefault(entrada =>
                    entrada.Intencion.Equals(
                        intencion,
                        StringComparison.Ordinal)
                    && entrada.Comando.Equals(
                        comando,
                        StringComparison.Ordinal));
            DateTimeOffset ahora =
                DateTimeOffset.UtcNow;

            if (existente is null)
            {
                entradas.RemoveAll(entrada =>
                    entrada.Intencion.Equals(
                        intencion,
                        StringComparison.Ordinal));
                entradas.Add(
                    new Entrada
                    {
                        Intencion = intencion,
                        Comando = comando,
                        Consulta = consulta,
                        Exitos = 1,
                        UltimoExitoUtc = ahora
                    });
            }
            else
            {
                existente.Exitos++;
                existente.UltimoExitoUtc = ahora;
            }

            _entradas = entradas
                .OrderByDescending(entrada =>
                    entrada.UltimoExitoUtc)
                .Take(MaximoEntradas)
                .ToList();

            string? carpeta =
                Path.GetDirectoryName(_ruta);

            if (!string.IsNullOrWhiteSpace(carpeta))
            {
                Directory.CreateDirectory(carpeta);
            }

            await File.WriteAllTextAsync(
                _ruta,
                JsonSerializer.Serialize(
                    new Almacen
                    {
                        Entradas = _entradas
                    },
                    OpcionesJson),
                cancellationToken);
            return true;
        }
        finally
        {
            _puerta.Release();
        }
    }

    private async Task CargarAsync(
        CancellationToken cancellationToken)
    {
        if (_entradas is not null)
        {
            return;
        }

        if (!File.Exists(_ruta))
        {
            _entradas = [];
            return;
        }

        try
        {
            string json =
                await File.ReadAllTextAsync(
                    _ruta,
                    cancellationToken);
            Almacen? almacen =
                JsonSerializer.Deserialize<Almacen>(
                    json,
                    OpcionesJson);
            _entradas = almacen?.Entradas
                ?.Where(entrada =>
                    !string.IsNullOrWhiteSpace(
                        entrada.Intencion)
                    && EsComandoValido(
                        entrada.Comando))
                .Take(MaximoEntradas)
                .ToList()
                ?? [];
        }
        catch (JsonException)
        {
            _entradas = [];
        }
    }

    private static bool EsComandoValido(
        string comando)
    {
        return ValidadorPowerShell.Validar(
                   comando)
               .Permitido
               && ControlWindows
                   .EsComandoCompatibleConModoConsola(
                       comando);
    }

    private static double Similitud(
        string intencion,
        HashSet<string> tokens,
        string candidata)
    {
        if (intencion.Equals(
                candidata,
                StringComparison.Ordinal))
        {
            return 1;
        }

        HashSet<string> otros =
            Tokens(candidata);

        if (tokens.Count == 0 || otros.Count == 0)
        {
            return 0;
        }

        int interseccion =
            tokens.Count(otros.Contains);
        int union =
            tokens.Count + otros.Count - interseccion;
        return union == 0
            ? 0
            : (double)interseccion / union;
    }

    private static HashSet<string> Tokens(
        string intencion)
    {
        return intencion
            .Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 2)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ObtenerRutaPredeterminada()
    {
        string carpeta =
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData);

        if (string.IsNullOrWhiteSpace(carpeta))
        {
            throw new InvalidOperationException(
                "Windows no devolvió la carpeta local de datos.");
        }

        return Path.Combine(
            carpeta,
            "ControlPCIA",
            "traducciones-v1.json");
    }

    private sealed class Almacen
    {
        public List<Entrada> Entradas { get; set; } = [];
    }

    private sealed class Entrada
    {
        public string Intencion { get; set; } = "";
        public string Comando { get; set; } = "";
        public bool Consulta { get; set; }
        public int Exitos { get; set; }
        public DateTimeOffset UltimoExitoUtc { get; set; }
    }
}
