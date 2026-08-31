using System.Text.RegularExpressions;

namespace ControlPCIA;

/// <summary>
/// Abre páginas y búsquedas web mediante el manejador HTTP/HTTPS
/// predeterminado de Windows. No inspecciona ni comprueba el navegador.
/// </summary>
internal static class ControlWebBasico
{
    private static readonly IReadOnlyDictionary<string, string>
        SitiosConocidos =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["youtube"] = "https://www.youtube.com/",
                ["youtube music"] = "https://music.youtube.com/",
                ["google"] = "https://www.google.com/",
                ["gmail"] = "https://mail.google.com/",
                ["facebook"] = "https://www.facebook.com/",
                ["instagram"] = "https://www.instagram.com/",
                ["twitter"] = "https://x.com/",
                ["x"] = "https://x.com/",
                ["tiktok"] = "https://www.tiktok.com/",
                ["twitch"] = "https://www.twitch.tv/",
                ["reddit"] = "https://www.reddit.com/",
                ["wikipedia"] = "https://es.wikipedia.org/",
                ["spotify"] = "https://open.spotify.com/",
                ["whatsapp"] = "https://web.whatsapp.com/",
                ["whatsapp web"] = "https://web.whatsapp.com/",
                ["linkedin"] = "https://www.linkedin.com/",
                ["github"] = "https://github.com/",
                ["openai"] = "https://openai.com/",
                ["chatgpt"] = "https://chatgpt.com/"
            };

    private static readonly Regex PeticionBusqueda = new(
        @"^(?:(?:oye|por favor)[\s,]+)*(?:(?:(?:quiero|necesito)(?:\s+que)?|puedes|podrias)\s+)?(?:busca(?:me)?|buscar|busques|encuentra(?:me)?|haz\s+una\s+busqueda(?:\s+de)?)\s+(?<resto>.+?)[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex AperturaPaginaExplicita = new(
        @"^(?:(?:oye|por favor)[\s,]+)*(?:(?:(?:quiero|necesito)(?:\s+que)?|puedes|podrias)\s+)?(?:abre(?:me)?|abrir|abras)\s+(?:(?:la|el|una|un)\s+)?(?:(?:pagina|web|sitio)(?:\s+web)?|url)(?:\s+de)?\s+(?<destino>.+?)[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex NavegacionExplicita = new(
        @"^(?:(?:oye|por favor)[\s,]+)*(?:(?:(?:quiero|necesito)(?:\s+que)?|puedes|podrias)\s+)?(?:entra\s+en|ve\s+a|visita|navega\s+a)\s+(?<destino>.+?)[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex AperturaGenerica = new(
        @"^(?:(?:oye|por favor)[\s,]+)*(?:(?:(?:quiero|necesito)(?:\s+que)?|puedes|podrias)\s+)?(?:abre(?:me)?|abrir|abras)\s+(?:(?:la|el|una|un)\s+)?(?<destino>.+?)[.!?]*$",
        RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    internal static PeticionBasica? Interpretar(
        string instruccion)
    {
        string normalizada =
            ControlBasico.Normalizar(instruccion)
                .Trim()
                .TrimStart('¿', '¡')
                .Trim();

        Match busqueda =
            PeticionBusqueda.Match(normalizada);

        if (busqueda.Success)
        {
            return CrearBusqueda(
                busqueda.Groups["resto"].Value);
        }

        Match pagina =
            AperturaPaginaExplicita.Match(normalizada);

        if (pagina.Success)
        {
            return CrearApertura(
                pagina.Groups["destino"].Value,
                permitirBusquedaAlternativa: true);
        }

        Match navegacion =
            NavegacionExplicita.Match(normalizada);

        if (navegacion.Success)
        {
            return CrearApertura(
                navegacion.Groups["destino"].Value,
                permitirBusquedaAlternativa: true);
        }

        Match apertura =
            AperturaGenerica.Match(normalizada);

        if (!apertura.Success)
        {
            return null;
        }

        string destino =
            LimpiarObjetivo(
                apertura.Groups["destino"].Value);

        return TryResolverPagina(
            destino,
            out string url,
            out string descripcion)
                ? new PeticionBasica(
                    TipoPeticionBasica.AbrirPaginaWeb,
                    url,
                    Descripcion: descripcion)
                : null;
    }

    internal static async Task<ResultadoControl>
        EjecutarAsync(
            PeticionBasica peticion,
            bool soloTraducir,
            DependenciasControlBasico dependencias,
            CancellationToken cancellationToken)
    {
        string comando =
            "Start-Process -FilePath "
            + EscaparLiteralPowerShell(
                peticion.Objetivo);
        string accion =
            peticion.Tipo
            == TipoPeticionBasica.BuscarEnInternet
                ? $"la búsqueda «{peticion.Descripcion}»"
                : $"la página {peticion.Descripcion}";

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                $"Abriría {accion} en el navegador predeterminado, pero el modo de prueba no ejecuta comandos.",
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
        ResultadoPasoControl paso =
            new(
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
                "error_al_abrir_web",
                "Windows no ha podido abrir el navegador predeterminado: "
                + ObtenerError(ejecucion),
                [paso],
                false);
        }

        string mensaje =
            $"He enviado al navegador predeterminado {accion}.";

        if (!string.IsNullOrWhiteSpace(ejecucion.Salida))
        {
            mensaje += "\nWindows ha respondido:\n"
                       + ejecucion.Salida.Trim();
        }

        return new ResultadoControl(
            true,
            "completado",
            mensaje,
            [paso],
            false);
    }

    private static PeticionBasica CrearBusqueda(
        string resto)
    {
        string contenido =
            LimpiarObjetivo(resto);
        string proveedor = "google";

        foreach (string candidato in new[]
                 {
                     "youtube",
                     "google",
                     "bing",
                     "internet",
                     "la web",
                     "web"
                 })
        {
            string prefijo = "en " + candidato + " ";
            string prefijoPor = "por " + candidato + " ";
            string sufijo = " en " + candidato;
            string porInternet = " por internet";

            if (contenido.StartsWith(
                    prefijo,
                    StringComparison.Ordinal))
            {
                proveedor = NormalizarProveedor(candidato);
                contenido = contenido[prefijo.Length..]
                    .Trim();
                break;
            }

            if (contenido.StartsWith(
                    prefijoPor,
                    StringComparison.Ordinal))
            {
                proveedor = NormalizarProveedor(candidato);
                contenido = contenido[prefijoPor.Length..]
                    .Trim();
                break;
            }

            if (contenido.EndsWith(
                    sufijo,
                    StringComparison.Ordinal))
            {
                proveedor = NormalizarProveedor(candidato);
                contenido = contenido[..^sufijo.Length]
                    .Trim();
                break;
            }

            if (candidato == "internet"
                && contenido.EndsWith(
                    porInternet,
                    StringComparison.Ordinal))
            {
                contenido = contenido[..^porInternet.Length]
                    .Trim();
                break;
            }
        }

        contenido = Regex.Replace(
                contenido,
                @"^(?:de|sobre)\s+",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();

        if (contenido.Length == 0)
        {
            return new PeticionBasica(
                TipoPeticionBasica.NoCompatible,
                Motivo:
                    "Dime qué quieres buscar por internet.");
        }

        string codificada =
            Uri.EscapeDataString(contenido);
        string url = proveedor switch
        {
            "youtube" =>
                "https://www.youtube.com/results?search_query="
                + codificada,
            "bing" =>
                "https://www.bing.com/search?q="
                + codificada,
            _ =>
                "https://www.google.com/search?q="
                + codificada
        };

        return new PeticionBasica(
            TipoPeticionBasica.BuscarEnInternet,
            url,
            Descripcion: contenido);
    }

    private static PeticionBasica CrearApertura(
        string destino,
        bool permitirBusquedaAlternativa)
    {
        string limpio =
            LimpiarObjetivo(destino);

        if (TryResolverPagina(
                limpio,
                out string url,
                out string descripcion))
        {
            return new PeticionBasica(
                TipoPeticionBasica.AbrirPaginaWeb,
                url,
                Descripcion: descripcion);
        }

        if (permitirBusquedaAlternativa
            && limpio.Length > 0)
        {
            return new PeticionBasica(
                TipoPeticionBasica.BuscarEnInternet,
                "https://www.google.com/search?q="
                + Uri.EscapeDataString(limpio),
                Descripcion: limpio);
        }

        return new PeticionBasica(
            TipoPeticionBasica.NoCompatible,
            Motivo:
                "Dime qué página quieres abrir.");
    }

    private static bool TryResolverPagina(
        string destino,
        out string url,
        out string descripcion)
    {
        url = string.Empty;
        descripcion = destino;
        string limpio =
            LimpiarObjetivo(destino);

        if (SitiosConocidos.TryGetValue(
                limpio,
                out string? conocido))
        {
            url = conocido;
            descripcion = limpio;
            return true;
        }

        if (!limpio.Contains(
                "://",
                StringComparison.Ordinal)
            && PareceDominio(limpio))
        {
            limpio = "https://" + limpio;
        }

        if (!Uri.TryCreate(
                limpio,
                UriKind.Absolute,
                out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        url = uri.AbsoluteUri;
        descripcion = uri.Host;
        return true;
    }

    private static bool PareceDominio(
        string texto)
    {
        return Regex.IsMatch(
            texto,
            @"^(?:www\.)?[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+(?:[/?#].*)?$",
            RegexOptions.CultureInvariant);
    }

    private static string LimpiarObjetivo(
        string texto)
    {
        return Regex.Replace(
                texto.Trim(),
                @"[\s,.]*(?:por favor)?[.!?]*$",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();
    }

    private static string NormalizarProveedor(
        string proveedor)
    {
        return proveedor is "internet" or "la web" or "web"
            ? "google"
            : proveedor;
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
               && string.IsNullOrWhiteSpace(resultado.Error);
    }

    private static string ObtenerError(
        ResultadoEjecucionPowerShell resultado)
    {
        return string.IsNullOrWhiteSpace(resultado.Error)
            ? $"código de salida {resultado.CodigoSalida}"
            : resultado.Error;
    }
}
