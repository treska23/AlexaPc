using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPCIA;

/// <summary>
/// Traduce lenguaje natural limitado sobre pantallas al comando de consola
/// propio de ControlPCIA. La ejecución real queda en ComandoPantallas.
/// </summary>
internal static class ControlPantallasBasico
{
    private static readonly Regex SelectorNumerico = new(
        @"\b(?:pantalla|monitor)(?:\s+(?:numero|num))?\s*(?<numero>\d+)\b",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex ResolucionNumerica = new(
        @"\b(?<ancho>\d{3,5})\s*(?:x|por)\s*(?<alto>\d{3,5})\b",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex FrecuenciaNumerica = new(
        @"\b(?<hz>\d{2,3})\s*(?:hz|hercios?)\b",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex EscalaNumerica = new(
        @"\b(?<escala>100|125|150|175|200|225|250|300|350|400|450|500)\s*(?:%|por\s+ciento)(?=\s|$)",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex PosicionNumerica = new(
        @"\bx\s*(?<x>-?\d+)\s*(?:,|y)?\s*y\s*(?<y>-?\d+)\b",
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

        if (!MencionaPantallas(texto))
        {
            return null;
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:resoluciones|modos)\b.*\b(?:soporta|admite|disponibles|tiene)\b|\b(?:que|cuales)\b.*\b(?:resoluciones|modos)\b",
                RegexOptions.CultureInvariant))
        {
            string selector = ObtenerSelector(texto);
            return Crear(
                $"modes {selector}",
                $"consultar los modos de la pantalla {DescribirSelector(selector)}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:que|cuales|cuantas|lista|listar|muestra|mostrar|dime|consulta|consultar|como)\b.*\b(?:pantallas?|monitor(?:es)?|configuracion)\b|\b(?:pantallas?|monitor(?:es)?)\b.*\b(?:tengo|hay|conectad[oa]s?|configurad[oa]s?)\b",
                RegexOptions.CultureInvariant)
            && !MencionaCambio(texto))
        {
            return Crear(
                "list",
                "consultar las pantallas conectadas");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:duplica|duplicar|duplicadas?|clona|clonar|replica|replicar|espejo)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "topology clone",
                "duplicar las pantallas");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:extiende|extender|extendidas?|modo\s+extendido)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "topology extend",
                "extender el escritorio entre las pantallas");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:solo|unicamente)\b.*\b(?:pantalla\s+del\s+pc|pantalla\s+interna|monitor\s+interno|portatil)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "topology internal",
                "usar únicamente la pantalla interna");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:solo|unicamente)\b.*\b(?:segunda\s+pantalla|pantalla\s+externa|monitor\s+externo|proyector|televisor)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "topology external",
                "usar únicamente la pantalla externa");
        }

        Match escala =
            EscalaNumerica.Match(texto);

        if (escala.Success
            && Regex.IsMatch(
                texto,
                @"\b(?:escala|escalado|tamano|zoom)\b",
                RegexOptions.CultureInvariant))
        {
            string selector = ObtenerSelector(texto);
            string porcentaje =
                escala.Groups["escala"].Value;
            return Crear(
                $"scale {selector} {porcentaje}",
                $"poner la escala de la pantalla {DescribirSelector(selector)} al {porcentaje}%");
        }

        Match resolucion =
            ResolucionNumerica.Match(texto);
        (int Ancho, int Alto)? aliasResolucion =
            resolucion.Success
                ? null
                : ObtenerResolucionConocida(texto);

        if (resolucion.Success
            || aliasResolucion is not null)
        {
            int ancho = resolucion.Success
                ? int.Parse(
                    resolucion.Groups["ancho"].Value)
                : aliasResolucion!.Value.Ancho;
            int alto = resolucion.Success
                ? int.Parse(
                    resolucion.Groups["alto"].Value)
                : aliasResolucion!.Value.Alto;
            string selector = ObtenerSelector(texto);
            Match frecuencia =
                FrecuenciaNumerica.Match(texto);
            string argumentoFrecuencia =
                frecuencia.Success
                    ? " " + frecuencia.Groups["hz"].Value
                    : string.Empty;
            return Crear(
                $"resolution {selector} {ancho} {alto}{argumentoFrecuencia}",
                $"poner la pantalla {DescribirSelector(selector)} a {ancho}x{alto}"
                + (frecuencia.Success
                    ? $" y {frecuencia.Groups["hz"].Value} Hz"
                    : string.Empty));
        }

        Match frecuenciaSola =
            FrecuenciaNumerica.Match(texto);

        if (frecuenciaSola.Success
            && Regex.IsMatch(
                texto,
                @"\b(?:frecuencia|refresco|hz|hercios|pon|cambia|configura)\b",
                RegexOptions.CultureInvariant))
        {
            string selector = ObtenerSelector(texto);
            string hz =
                frecuenciaSola.Groups["hz"].Value;
            return Crear(
                $"frequency {selector} {hz}",
                $"poner la pantalla {DescribirSelector(selector)} a {hz} Hz");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:principal)\b",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                texto,
                @"\b(?:pon|poner|haz|hacer|elige|elegir|establece|establecer|configura|cambiar|cambia|convierte|convertir|usa|usar|quiero|sea)\b",
                RegexOptions.CultureInvariant))
        {
            string selector =
                ObtenerSelector(
                    texto,
                    permitirPrincipal: false);

            if (selector == "primary")
            {
                return NoCompatible(
                    "Dime qué número de pantalla quieres convertir en principal.");
            }

            return Crear(
                $"primary {selector}",
                $"convertir la pantalla {selector} en principal");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:desactiva|desactivar|desconecta|desconectar|deshabilita|deshabilitar|inhabilita|inhabilitar|apaga|apagar)\b",
                RegexOptions.CultureInvariant))
        {
            string selector = ObtenerSelector(texto);
            return Crear(
                $"disable {selector}",
                $"desactivar la pantalla {DescribirSelector(selector)}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:activa|activar|reactiva|reactivar|conecta|conectar|habilita|habilitar|enciende|encender)\b",
                RegexOptions.CultureInvariant))
        {
            string selector = ObtenerSelector(texto);
            return Crear(
                $"enable {selector}",
                $"activar la pantalla {DescribirSelector(selector)}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:orientacion|orienta|orientar|vertical|horizontal|gira|girar)\b",
                RegexOptions.CultureInvariant))
        {
            string selector = ObtenerSelector(texto);
            string orientacion =
                Regex.IsMatch(
                    texto,
                    @"\b(?:270|doscientos\s+setenta)\s*grados?\b",
                    RegexOptions.CultureInvariant)
                    ? "portrait-flipped"
                    : Regex.IsMatch(
                        texto,
                        @"\b(?:180|ciento\s+ochenta)\s*grados?\b",
                        RegexOptions.CultureInvariant)
                        ? "landscape-flipped"
                        : Regex.IsMatch(
                            texto,
                            @"\b(?:90|noventa)\s*grados?\b",
                            RegexOptions.CultureInvariant)
                            ? "portrait"
                            : Regex.IsMatch(
                    texto,
                    @"\bvertical\b.*\b(?:invertida|invertido|al\s+reves)\b|\b(?:invertida|invertido|al\s+reves)\b.*\bvertical\b",
                    RegexOptions.CultureInvariant)
                    ? "portrait-flipped"
                    : Regex.IsMatch(
                        texto,
                        @"\bhorizontal\b.*\b(?:invertida|invertido|al\s+reves)\b|\b(?:invertida|invertido|al\s+reves)\b.*\bhorizontal\b",
                        RegexOptions.CultureInvariant)
                        ? "landscape-flipped"
                        : Regex.IsMatch(
                            texto,
                            @"\bvertical\b",
                            RegexOptions.CultureInvariant)
                            ? "portrait"
                            : "landscape";
            return Crear(
                $"orientation {selector} {orientacion}",
                $"cambiar la orientación de la pantalla {DescribirSelector(selector)}");
        }

        Match posicion =
            PosicionNumerica.Match(texto);

        if (posicion.Success
            && Regex.IsMatch(
                texto,
                @"\b(?:posicion|coloca|colocar|mueve|mover)\b",
                RegexOptions.CultureInvariant))
        {
            string selector = ObtenerSelector(texto);
            return Crear(
                $"position {selector} {posicion.Groups["x"].Value} {posicion.Groups["y"].Value}",
                $"colocar la pantalla {DescribirSelector(selector)} en las coordenadas indicadas");
        }

        MatchCollection selectores =
            SelectorNumerico.Matches(texto);
        Match relativa = Regex.Match(
            texto,
            @"\b(?:a\s+la\s+)?(?<lado>izquierda|derecha|arriba|encima|abajo|debajo)\b",
            RegexOptions.CultureInvariant);

        if (relativa.Success
            && selectores.Count >= 2
            && Regex.IsMatch(
                texto,
                @"\b(?:coloca|colocar|mueve|mover|pon|poner|situa|situar)\b",
                RegexOptions.CultureInvariant))
        {
            string lado =
                relativa.Groups["lado"].Value switch
                {
                    "izquierda" => "left",
                    "derecha" => "right",
                    "arriba" or "encima" => "above",
                    _ => "below"
                };
            string objetivo =
                selectores[0].Groups["numero"].Value;
            string referencia =
                selectores[1].Groups["numero"].Value;
            return Crear(
                $"place {objetivo} {lado} {referencia}",
                $"colocar la pantalla {objetivo} respecto a la pantalla {referencia}");
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
            "ControlPCIA.exe display "
            + peticion.Objetivo;

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                $"Cambiaría o consultaría la configuración para {peticion.Descripcion}, pero el modo de prueba no ejecuta comandos.",
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
                "error_configuracion_pantallas",
                ObtenerDetalle(
                    ejecucion.Error,
                    "Windows no ha aceptado la orden de pantalla."),
                [paso],
                false);
        }

        string mensaje =
            peticion.Objetivo == "list"
                ? FormatearPantallas(ejecucion.Salida)
                : peticion.Objetivo.StartsWith(
                    "modes ",
                    StringComparison.Ordinal)
                    ? FormatearModos(ejecucion.Salida)
                    : ObtenerDetalle(
                        ejecucion.Salida,
                        $"Windows ha aceptado la orden para {peticion.Descripcion}.");

        return new ResultadoControl(
            true,
            peticion.Objetivo is "list"
                || peticion.Objetivo.StartsWith(
                    "modes ",
                    StringComparison.Ordinal)
                    ? "respuesta"
                    : "completado",
            mensaje,
            [paso],
            false);
    }

    private static bool MencionaPantallas(
        string texto)
    {
        return Regex.IsMatch(
            texto,
            @"\b(?:pantallas?|monitor(?:es)?|escritorio|proyector|televisor|resolucion(?:es)?|definicion|frecuencia|refresco|hercios|hz|orientacion|escala|escalado|zoom)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool MencionaCambio(
        string texto)
    {
        return Regex.IsMatch(
            texto,
            @"\b(?:pon|poner|haz|hacer|elige|elegir|establece|establecer|configura|configurar|cambia|cambiar|convierte|convertir|usa|usar|quiero|sea|desactiva|desactivar|deshabilita|deshabilitar|activa|activar|reactiva|reactivar|conecta|conectar|desconecta|desconectar|apaga|apagar|enciende|encender|duplica|duplicar|clona|clonar|replica|replicar|extiende|extender|coloca|colocar|mueve|mover|gira|girar)\b",
            RegexOptions.CultureInvariant);
    }

    private static string ObtenerSelector(
        string texto,
        bool permitirPrincipal = true)
    {
        Match numerico =
            SelectorNumerico.Match(texto);

        if (numerico.Success)
        {
            return numerico.Groups["numero"].Value;
        }

        Match ordinal = Regex.Match(
            texto,
            @"\b(?:pantalla|monitor)\s+(?:numero\s+)?(?<ordinal>uno|una|primera|primero|dos|segunda|segundo|tres|tercera|tercero|cuatro|cuarta|cuarto)\b|\b(?<ordinal2>primera|primer|segunda|segundo|tercera|tercer|cuarta|cuarto)\s+(?:pantalla|monitor)\b",
            RegexOptions.CultureInvariant);

        if (ordinal.Success)
        {
            string valor =
                ordinal.Groups["ordinal"].Success
                    ? ordinal.Groups["ordinal"].Value
                    : ordinal.Groups["ordinal2"].Value;
            return valor switch
            {
                "uno" or "una" or "primera" or "primero" or "primer" => "1",
                "dos" or "segunda" or "segundo" => "2",
                "tres" or "tercera" or "tercero" or "tercer" => "3",
                _ => "4"
            };
        }

        return permitirPrincipal
               && Regex.IsMatch(
                   texto,
                   @"\bprincipal\b",
                   RegexOptions.CultureInvariant)
            ? "primary"
            : "primary";
    }

    private static (int Ancho, int Alto)? ObtenerResolucionConocida(
        string texto)
    {
        if (Regex.IsMatch(
                texto,
                @"\b(?:4k|2160p)\b",
                RegexOptions.CultureInvariant))
        {
            return (3840, 2160);
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:2k|1440p|qhd)\b",
                RegexOptions.CultureInvariant))
        {
            return (2560, 1440);
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:full\s*hd|1080p)\b",
                RegexOptions.CultureInvariant))
        {
            return (1920, 1080);
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:hd|720p)\b",
                RegexOptions.CultureInvariant))
        {
            return (1280, 720);
        }

        return null;
    }

    private static PeticionBasica Crear(
        string argumentos,
        string descripcion)
    {
        return new PeticionBasica(
            TipoPeticionBasica.GestionarPantallas,
            argumentos,
            Descripcion: descripcion);
    }

    private static PeticionBasica NoCompatible(
        string motivo)
    {
        return new PeticionBasica(
            TipoPeticionBasica.NoCompatible,
            Motivo: motivo);
    }

    private static string DescribirSelector(
        string selector)
    {
        return selector == "primary"
            ? "principal"
            : selector;
    }

    private static bool EsCorrecto(
        ResultadoEjecucionPowerShell resultado)
    {
        return resultado.Ejecutado
               && resultado.CodigoSalida == 0
               && string.IsNullOrWhiteSpace(resultado.Error);
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
                && detalle.ValueKind == JsonValueKind.String
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

    private static string FormatearPantallas(
        string json)
    {
        try
        {
            using JsonDocument documento =
                JsonDocument.Parse(json);
            JsonElement pantallas =
                documento.RootElement.GetProperty(
                    "pantallas");
            int cantidad =
                pantallas.GetArrayLength();
            var mensaje = new StringBuilder();
            mensaje.Append(
                cantidad == 1
                    ? "Windows detecta una pantalla:"
                    : $"Windows detecta {cantidad} pantallas:");

            foreach (JsonElement pantalla in
                     pantallas.EnumerateArray())
            {
                int numero =
                    pantalla.GetProperty("Numero").GetInt32();
                bool activa =
                    pantalla.GetProperty("Activa").GetBoolean();
                bool principal =
                    pantalla.GetProperty("Principal").GetBoolean();
                string monitor =
                    pantalla.GetProperty("Monitor").GetString()
                    ?? "Monitor";
                mensaje.AppendLine();
                mensaje.Append("- Pantalla ");
                mensaje.Append(numero);
                mensaje.Append(" — ");
                mensaje.Append(monitor);
                mensaje.Append(activa ? ", activa" : ", desactivada");

                if (principal)
                {
                    mensaje.Append(", principal");
                }

                if (activa)
                {
                    mensaje.Append(", ");
                    mensaje.Append(
                        pantalla.GetProperty("Ancho").GetInt32());
                    mensaje.Append('x');
                    mensaje.Append(
                        pantalla.GetProperty("Alto").GetInt32());
                    mensaje.Append(" a ");
                    mensaje.Append(
                        pantalla.GetProperty("Frecuencia").GetInt32());
                    mensaje.Append(" Hz");
                    if (pantalla.TryGetProperty(
                            "Escala",
                            out JsonElement escala)
                        && escala.ValueKind
                        == JsonValueKind.Number)
                    {
                        mensaje.Append(", escala ");
                        mensaje.Append(
                            escala.GetInt32());
                        mensaje.Append('%');
                    }
                    mensaje.Append(", posición ");
                    mensaje.Append(
                        pantalla.GetProperty("X").GetInt32());
                    mensaje.Append(',');
                    mensaje.Append(
                        pantalla.GetProperty("Y").GetInt32());
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
                "Consulta de pantallas completada.");
        }
    }

    private static string FormatearModos(
        string json)
    {
        try
        {
            using JsonDocument documento =
                JsonDocument.Parse(json);
            JsonElement raiz =
                documento.RootElement;
            int pantalla =
                raiz.GetProperty("pantalla").GetInt32();
            IEnumerable<IGrouping<
                string,
                JsonElement>> grupos =
                raiz.GetProperty("modos")
                    .EnumerateArray()
                    .GroupBy(
                        modo =>
                            modo.GetProperty("Ancho").GetInt32()
                            + "x"
                            + modo.GetProperty("Alto").GetInt32());
            var mensaje = new StringBuilder(
                $"Modos disponibles para la pantalla {pantalla}:");

            foreach (IGrouping<string, JsonElement> grupo in grupos)
            {
                string frecuencias = string.Join(
                    ", ",
                    grupo.Select(modo =>
                            modo.GetProperty("Frecuencia").GetInt32())
                        .Distinct()
                        .OrderBy(valor => valor));
                mensaje.AppendLine();
                mensaje.Append("- ");
                mensaje.Append(grupo.Key);
                mensaje.Append(": ");
                mensaje.Append(frecuencias);
                mensaje.Append(" Hz");
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
                "Consulta de modos completada.");
        }
    }
}
