using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPCIA;

/// <summary>
/// Traduce órdenes de reproducción al transporte multimedia global de
/// Windows. Las aplicaciones deciden qué operaciones admiten.
/// </summary>
internal static class ControlMultimediaBasico
{
    private static readonly Regex Segundos = new(
        @"\b(?<segundos>\d+(?:[.,]\d+)?)\s*segundos?\b",
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

        if (!MencionaMultimedia(texto))
        {
            return null;
        }

        string aplicacion =
            ObtenerAplicacion(texto);
        string argumentoAplicacion =
            aplicacion.Length == 0
                ? string.Empty
                : " --app "
                  + EscaparLiteralPowerShell(
                      aplicacion);
        string descripcionAplicacion =
            aplicacion.Length == 0
                ? "la sesión multimedia actual"
                : aplicacion == "browser"
                    ? "el navegador"
                    : aplicacion;

        if (Regex.IsMatch(
                texto,
                @"\b(?:pantalla\s+completa|modo\s+pantalla\s+completa|fullscreen)\b",
                RegexOptions.CultureInvariant))
        {
            bool salir =
                Regex.IsMatch(
                    texto,
                    @"\b(?:sal|salir|quita|quitar|desactiva|desactivar|cierra|cerrar)\b",
                    RegexOptions.CultureInvariant);
            string navegador =
                aplicacion.Length == 0
                    ? "browser"
                    : aplicacion;
            string argumentoNavegador =
                " --app "
                + EscaparLiteralPowerShell(
                    navegador);
            return Crear(
                (salir
                    ? "exit-fullscreen"
                    : "fullscreen")
                + argumentoNavegador,
                salir
                    ? $"salir de la pantalla completa en {descripcionAplicacion}"
                    : $"poner el vídeo en pantalla completa en {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:que|cual|dime|muestra|consulta)\b.*\b(?:reproduciendo|reproduccion|cancion|video|musica)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "status" + argumentoAplicacion,
                $"consultar {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:siguiente|proxima|proximo|salta\s+a\s+la\s+siguiente)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "next" + argumentoAplicacion,
                $"pasar al contenido siguiente en {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:anterior|previa|previo|vuelve\s+a\s+la\s+anterior)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "previous" + argumentoAplicacion,
                $"volver al contenido anterior en {descripcionAplicacion}");
        }

        Match segundos =
            Segundos.Match(texto);

        if (segundos.Success
            && Regex.IsMatch(
                texto,
                @"\b(?:adelanta|adelantar|avanza|avanzar|salta)\b",
                RegexOptions.CultureInvariant))
        {
            string valor =
                segundos.Groups["segundos"].Value
                    .Replace(',', '.');
            return Crear(
                $"seek {valor}{argumentoAplicacion}",
                $"adelantar {valor} segundos en {descripcionAplicacion}");
        }

        if (segundos.Success
            && Regex.IsMatch(
                texto,
                @"\b(?:retrocede|retroceder|atrasa|atrasar)\b",
                RegexOptions.CultureInvariant))
        {
            string valor =
                segundos.Groups["segundos"].Value
                    .Replace(',', '.');
            return Crear(
                $"seek -{valor}{argumentoAplicacion}",
                $"retroceder {valor} segundos en {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:pausa|pausar|pausala|pausalo)\b|\bpara\b.*\b(?:reproduccion|video|cancion|musica)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "pause" + argumentoAplicacion,
                $"pausar {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:deten|detener|parar)\b.*\b(?:reproduccion|video|cancion|musica)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "stop" + argumentoAplicacion,
                $"detener {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:reanuda|reanudar|reproduce|reproducir|continua|continuar)\b|\b(?:haz|dale)\s+(?:al\s+)?play\b|\bplay\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "play" + argumentoAplicacion,
                $"reproducir {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:alterna|alternar|cambia)\b.*\b(?:play|pausa|reproduccion)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "toggle" + argumentoAplicacion,
                $"alternar reproducción y pausa en {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:activa|activar|pon|poner)\b.*\b(?:aleatorio|shuffle)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "shuffle on" + argumentoAplicacion,
                $"activar la reproducción aleatoria en {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:desactiva|desactivar|quita|quitar)\b.*\b(?:aleatorio|shuffle)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "shuffle off" + argumentoAplicacion,
                $"desactivar la reproducción aleatoria en {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:repite|repetir|bucle)\b.*\b(?:cancion|pista|tema)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "repeat track" + argumentoAplicacion,
                $"repetir la pista en {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:repite|repetir|bucle)\b.*\b(?:lista|playlist)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "repeat list" + argumentoAplicacion,
                $"repetir la lista en {descripcionAplicacion}");
        }

        if (Regex.IsMatch(
                texto,
                @"\b(?:quita|quitar|desactiva|desactivar)\b.*\b(?:repeticion|bucle)\b",
                RegexOptions.CultureInvariant))
        {
            return Crear(
                "repeat off" + argumentoAplicacion,
                $"desactivar la repetición en {descripcionAplicacion}");
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
            "ControlPCIA.exe media "
            + peticion.Objetivo;

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                $"Controlaría {peticion.Descripcion}, pero el modo de prueba no ejecuta comandos.",
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
                "error_control_multimedia",
                ObtenerDetalle(
                    ejecucion.Error,
                    "La aplicación no ha aceptado la orden multimedia."),
                [paso],
                false);
        }

        string mensaje =
            peticion.Objetivo.StartsWith(
                "status",
                StringComparison.Ordinal)
                ? FormatearEstado(
                    ejecucion.Salida)
                : ObtenerDetalle(
                    ejecucion.Salida,
                    $"La aplicación ha aceptado la orden para {peticion.Descripcion}.");

        return new ResultadoControl(
            true,
            peticion.Objetivo.StartsWith(
                "status",
                StringComparison.Ordinal)
                ? "respuesta"
                : "completado",
            mensaje,
            [paso],
            false);
    }

    private static bool MencionaMultimedia(
        string texto)
    {
        return Regex.IsMatch(
            texto,
            @"\b(?:reproduccion|reproduciendo|reproduce|reproducir|reanuda|reanudar|pausa|pausar|pausala|pausalo|play|video|videos|cancion|canciones|musica|spotify|youtube|multimedia|pista|playlist|aleatorio|shuffle|pantalla\s+completa|fullscreen)\b",
            RegexOptions.CultureInvariant);
    }

    private static string ObtenerAplicacion(
        string texto)
    {
        if (Regex.IsMatch(
                texto,
                @"\bspotify\b",
                RegexOptions.CultureInvariant))
        {
            return "spotify";
        }

        foreach (string aplicacion in new[]
                 {
                     "vlc",
                     "musicbee",
                     "foobar",
                     "itunes",
                     "windows media player",
                     "media player"
                 })
        {
            if (texto.Contains(
                    aplicacion,
                    StringComparison.Ordinal))
            {
                return aplicacion;
            }
        }

        foreach (string navegador in new[]
                 {
                     "edge",
                     "chrome",
                     "firefox",
                     "brave",
                     "opera",
                     "vivaldi"
                 })
        {
            if (Regex.IsMatch(
                    texto,
                    $@"\b{Regex.Escape(navegador)}\b",
                    RegexOptions.CultureInvariant))
            {
                return navegador;
            }
        }

        return Regex.IsMatch(
            texto,
            @"\b(?:navegador|internet|youtube|pagina\s+web|video\s+que\s+estoy\s+viendo)\b",
            RegexOptions.CultureInvariant)
                ? "browser"
                : string.Empty;
    }

    private static PeticionBasica Crear(
        string argumentos,
        string descripcion)
    {
        return new PeticionBasica(
            TipoPeticionBasica.ControlarMultimedia,
            argumentos,
            Descripcion: descripcion);
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

    private static string FormatearEstado(
        string json)
    {
        try
        {
            using JsonDocument documento =
                JsonDocument.Parse(json);
            JsonElement sesion =
                documento.RootElement.GetProperty(
                    "sesion");
            string aplicacion =
                sesion.GetProperty("aplicacion")
                    .GetString()
                ?? "aplicación";
            string titulo =
                sesion.GetProperty("titulo")
                    .GetString()
                ?? string.Empty;
            string artista =
                sesion.GetProperty("artista")
                    .GetString()
                ?? string.Empty;
            string estado =
                sesion.GetProperty("estado")
                    .GetString()
                ?? "desconocido";
            var mensaje = new StringBuilder();
            mensaje.Append("Sesión multimedia de ");
            mensaje.Append(aplicacion);
            mensaje.Append(": ");
            mensaje.Append(
                TraducirEstado(estado));

            if (titulo.Length > 0)
            {
                mensaje.Append(". ");
                mensaje.Append(titulo);
            }

            if (artista.Length > 0)
            {
                mensaje.Append(" — ");
                mensaje.Append(artista);
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
                "Consulta multimedia completada.");
        }
    }

    private static string TraducirEstado(
        string estado)
    {
        return estado.ToLowerInvariant() switch
        {
            "playing" => "reproduciendo",
            "paused" => "en pausa",
            "stopped" => "detenida",
            "closed" => "cerrada",
            "changing" => "cambiando",
            "opened" => "abierta",
            _ => estado
        };
    }
}
