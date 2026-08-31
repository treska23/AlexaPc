using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPCIA;

/// <summary>
/// Traduce órdenes naturales sobre ventanas superiores al comando de consola
/// propio de ControlPCIA. No usa capturas, OCR, ratón ni teclado.
/// </summary>
internal static class ControlVentanasBasico
{
    private static readonly Regex Cerrar = new(
        @"^(?:(?:por\s+favor)\s+)?(?:cierra|cerrar|sal\s+de)\s+(?<objetivo>.+?)\s*(?:por\s+favor)?[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex Maximizar = new(
        @"^(?:(?:por\s+favor)\s+)?(?:(?:maximiza|maximizar)\s+(?<objetivo>.+?)|(?:pon|deja)\s+(?<objetivo>.+?)\s+(?:maximizada|maximizado|a\s+pantalla\s+completa))[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex Minimizar = new(
        @"^(?:(?:por\s+favor)\s+)?(?:(?:minimiza|minimizar)\s+(?<objetivo>.+?)|(?:pon|deja)\s+(?<objetivo>.+?)\s+(?:minimizada|minimizado))[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex Restaurar = new(
        @"^(?:(?:por\s+favor)\s+)?(?:(?:restaura|restaurar|normaliza|normalizar)\s+(?<objetivo>.+?)|(?:pon|deja)\s+(?<objetivo>.+?)\s+(?:normal|restaurada|restaurado))[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex PrimerPlano = new(
        @"^(?:(?:por\s+favor)\s+)?(?:(?:trae|lleva)\s+(?<objetivo>.+?)\s+(?:al\s+frente|a\s+primer\s+plano|delante)|(?:pon|deja|activa)\s+(?<objetivo>.+?)\s+(?:al\s+frente|en\s+primer\s+plano|delante)|(?:trae\s+al\s+frente|activa)\s+(?<objetivo>.+?))[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex Colocar = new(
        @"^(?:(?:por\s+favor)\s+)?(?:mueve|mover|coloca|colocar|pon)\s+(?<objetivo>.+?)\s+(?:en\s+)?x\s*(?<x>-?\d+)\s*(?:,|y)?\s*y\s*(?<y>-?\d+)\s+(?:con\s+)?(?:ancho|anchura)\s*(?<ancho>\d+)\s+(?:y\s+)?(?:alto|altura)\s*(?<alto>\d+)[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex Consultar = new(
        @"^(?:donde\s+esta|donde\s+tengo|muestra|consulta|dime\s+el\s+estado\s+de)\s+(?<objetivo>.+?)[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    internal static PeticionBasica? Interpretar(
        string instruccion)
    {
        string texto =
            ControlBasico.Normalizar(instruccion)
                .Trim()
                .TrimStart('¿', '¡')
                .Trim();

        Match colocacion =
            Colocar.Match(texto);

        if (colocacion.Success)
        {
            string objetivo =
                LimpiarObjetivo(
                    colocacion.Groups["objetivo"].Value);

            if (objetivo.Length == 0)
            {
                return FaltaObjetivo();
            }

            return Crear(
                objetivo,
                $"--x {colocacion.Groups["x"].Value} "
                + $"--y {colocacion.Groups["y"].Value} "
                + $"--width {colocacion.Groups["ancho"].Value} "
                + $"--height {colocacion.Groups["alto"].Value} "
                + "--foreground",
                $"colocar la ventana de {objetivo}");
        }

        foreach ((Regex Patron, string Argumentos, string Accion)
                 in new[]
                 {
                     (
                         Cerrar,
                         "--close",
                         "cerrar"),
                     (
                         Maximizar,
                         "--state maximized --foreground",
                         "maximizar"),
                     (
                         Minimizar,
                         "--state minimized",
                         "minimizar"),
                     (
                         Restaurar,
                         "--state normal --foreground",
                         "restaurar"),
                     (
                         PrimerPlano,
                         "--foreground",
                         "traer al frente")
                 })
        {
            Match coincidencia =
                Patron.Match(texto);

            if (!coincidencia.Success)
            {
                continue;
            }

            string objetivo =
                LimpiarObjetivo(
                    coincidencia.Groups["objetivo"].Value);

            if (objetivo.Length == 0)
            {
                return FaltaObjetivo();
            }

            return Crear(
                objetivo,
                Argumentos,
                $"{Accion} la ventana de {objetivo}");
        }

        Match consulta =
            Consultar.Match(texto);

        if (consulta.Success
            && Regex.IsMatch(
                texto,
                @"\b(?:ventana|aplicacion|programa|calculadora|edge|chrome|cubase|visual\s+studio)\b",
                RegexOptions.CultureInvariant))
        {
            string objetivo =
                LimpiarObjetivo(
                    consulta.Groups["objetivo"].Value);

            if (objetivo.Length > 0)
            {
                return Crear(
                    objetivo,
                    "--list",
                    $"consultar la ventana de {objetivo}");
            }
        }

        return null;
    }

    internal static async Task<ResultadoControl> EjecutarAsync(
        PeticionBasica peticion,
        bool soloTraducir,
        DependenciasControlBasico dependencias,
        CancellationToken cancellationToken)
    {
        string comando =
            "ControlPCIA.exe window --match "
            + EscaparLiteralPowerShell(
                peticion.Objetivo)
            + " "
            + peticion.Motivo;

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                $"Controlaría la ventana para {peticion.Descripcion}, pero el modo de prueba no ejecuta comandos.",
                [
                    new ResultadoPasoControl(
                        1,
                        comando,
                        false,
                        0,
                        string.Empty,
                        string.Empty)
                ],
                false);
        }

        ResultadoEjecucionPowerShell ejecucion =
            await dependencias.EjecutarAsync(
                comando,
                cancellationToken);
        ResultadoPasoControl paso = new(
            1,
            comando,
            ejecucion.Ejecutado,
            ejecucion.CodigoSalida,
            ejecucion.Salida,
            ejecucion.Error);

        if (!EsCorrecto(ejecucion))
        {
            return new ResultadoControl(
                false,
                "error_control_ventana",
                ObtenerDetalle(
                    ejecucion.Error,
                    "Windows no ha aceptado la orden sobre la ventana."),
                [paso],
                false);
        }

        bool consulta =
            peticion.Motivo.Equals(
                "--list",
                StringComparison.Ordinal);
        string mensaje =
            consulta
                ? FormatearConsulta(
                    ejecucion.Salida,
                    peticion.Objetivo)
                : ObtenerDetalle(
                    ejecucion.Salida,
                    $"Windows ha aceptado la orden para {peticion.Descripcion}.");

        return new ResultadoControl(
            true,
            consulta ? "respuesta" : "completado",
            mensaje,
            [paso],
            false);
    }

    private static PeticionBasica Crear(
        string objetivo,
        string argumentos,
        string descripcion)
    {
        return new PeticionBasica(
            TipoPeticionBasica.GestionarVentanas,
            objetivo,
            argumentos,
            descripcion);
    }

    private static PeticionBasica FaltaObjetivo()
    {
        return new PeticionBasica(
            TipoPeticionBasica.NoCompatible,
            Motivo:
                "Dime qué aplicación o ventana quieres controlar.");
    }

    private static string LimpiarObjetivo(
        string objetivo)
    {
        string limpio =
            objetivo.Trim();
        limpio = Regex.Replace(
                limpio,
                @"^(?:(?:la|el|una|un)\s+)?(?:(?:aplicacion|programa|ventana)\s+(?:de|del)?\s*)?",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();
        limpio = Regex.Replace(
                limpio,
                @"\s*(?:por\s+favor)?[.!?]*$",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();
        return limpio;
    }

    private static string EscaparLiteralPowerShell(
        string valor)
    {
        return "'"
               + valor.Replace(
                   "'",
                   "''",
                   StringComparison.Ordinal)
               + "'";
    }

    private static bool EsCorrecto(
        ResultadoEjecucionPowerShell resultado)
    {
        return resultado.Ejecutado
               && resultado.CodigoSalida == 0
               && string.IsNullOrWhiteSpace(
                   resultado.Error);
    }

    private static string ObtenerDetalle(
        string json,
        string alternativa)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return alternativa;
        }

        try
        {
            using JsonDocument documento =
                JsonDocument.Parse(json);

            if (documento.RootElement.TryGetProperty(
                    "detalle",
                    out JsonElement detalle)
                && detalle.ValueKind
                == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(
                    detalle.GetString()))
            {
                return detalle.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return json.Trim();
    }

    private static string FormatearConsulta(
        string json,
        string objetivo)
    {
        try
        {
            using JsonDocument documento =
                JsonDocument.Parse(json);
            JsonElement raiz =
                documento.RootElement;
            JsonElement ventanas =
                raiz.GetProperty("ventanas");

            if (ventanas.GetArrayLength() == 0)
            {
                return $"No hay ninguna ventana abierta que coincida con «{objetivo}».";
            }

            var mensaje =
                new StringBuilder();
            mensaje.Append(
                ventanas.GetArrayLength() == 1
                    ? "He encontrado una ventana:"
                    : $"He encontrado {ventanas.GetArrayLength()} ventanas:");

            foreach (JsonElement ventana in
                     ventanas.EnumerateArray())
            {
                mensaje.AppendLine();
                mensaje.Append("- ");
                mensaje.Append(
                    ventana.GetProperty("titulo").GetString()
                    ?? ventana.GetProperty("proceso").GetString()
                    ?? objetivo);
                mensaje.Append(", ");
                mensaje.Append(
                    ventana.GetProperty("estado").GetString()
                    ?? "estado desconocido");

                if (ventana.TryGetProperty(
                        "primerPlano",
                        out JsonElement primerPlano)
                    && primerPlano.ValueKind
                    == JsonValueKind.True)
                {
                    mensaje.Append(", en primer plano");
                }
            }

            return mensaje.ToString();
        }
        catch (Exception ex) when (
            ex is JsonException
                or KeyNotFoundException
                or InvalidOperationException)
        {
            return ObtenerDetalle(
                json,
                "Consulta de ventanas completada.");
        }
    }
}
