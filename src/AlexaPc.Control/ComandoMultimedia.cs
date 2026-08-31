using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Media;
using Windows.Media.Control;

namespace ControlPCIA;

internal enum AccionComandoMultimedia
{
    Listar,
    Estado,
    Reproducir,
    Pausar,
    Alternar,
    Detener,
    Siguiente,
    Anterior,
    Avanzar,
    Retroceder,
    Desplazar,
    Aleatorio,
    Repeticion,
    Velocidad,
    PantallaCompleta,
    SalirPantallaCompleta
}

internal sealed record OpcionesComandoMultimedia(
    AccionComandoMultimedia Accion,
    string Aplicacion = "",
    double? Valor = null,
    string Modo = "");

/// <summary>
/// Controla sesiones multimedia publicadas por las aplicaciones en el
/// transporte multimedia global de Windows. Para la pantalla completa del
/// vídeo envía únicamente F o Escape al navegador mediante SendInput.
/// </summary>
internal static class ComandoMultimedia
{
    public static async Task<int> EjecutarAsync(
        IReadOnlyList<string> argumentos,
        TextWriter salida,
        TextWriter error)
    {
        if (!TryAnalizar(
                argumentos,
                out OpcionesComandoMultimedia? opciones,
                out string errorArgumentos))
        {
            await error.WriteLineAsync(errorArgumentos);
            return 2;
        }

        try
        {
            if (opciones!.Accion
                is AccionComandoMultimedia.PantallaCompleta
                or AccionComandoMultimedia.SalirPantallaCompleta)
            {
                return await EjecutarPantallaCompletaAsync(
                    opciones,
                    salida,
                    error);
            }

            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync();
            IReadOnlyList<
                GlobalSystemMediaTransportControlsSession> sesiones =
                    manager.GetSessions().ToArray();

            if (opciones!.Accion
                == AccionComandoMultimedia.Listar)
            {
                object[] elementos =
                    await Task.WhenAll(
                        sesiones.Select(
                            CrearResumenAsync));
                await salida.WriteLineAsync(
                    JsonSerializer.Serialize(
                        new
                        {
                            correcto = true,
                            detalle =
                                "Consulta de sesiones multimedia completada.",
                            sesiones = elementos
                        }));
                return 0;
            }

            GlobalSystemMediaTransportControlsSession? sesion =
                SeleccionarSesion(
                    manager,
                    sesiones,
                    opciones.Aplicacion);

            if (sesion is null)
            {
                await error.WriteLineAsync(
                    JsonSerializer.Serialize(
                        new
                        {
                            correcto = false,
                            detalle = string.IsNullOrWhiteSpace(
                                opciones.Aplicacion)
                                ? "Windows no tiene ninguna sesión multimedia activa."
                                : $"Windows no encuentra una sesión multimedia de «{opciones.Aplicacion}»."
                        }));
                return 4;
            }

            if (opciones.Accion
                == AccionComandoMultimedia.Estado)
            {
                await salida.WriteLineAsync(
                    JsonSerializer.Serialize(
                        new
                        {
                            correcto = true,
                            detalle =
                                "Consulta multimedia completada.",
                            sesion =
                                await CrearResumenAsync(
                                    sesion)
                        }));
                return 0;
            }

            bool correcto =
                await EjecutarAccionAsync(
                    sesion,
                    opciones);
            object resumen =
                await CrearResumenAsync(sesion);
            TextWriter destino =
                correcto ? salida : error;
            await destino.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        correcto,
                        detalle = correcto
                            ? "La aplicación multimedia aceptó la orden."
                            : "La sesión existe, pero la aplicación no admite o rechazó esa orden multimedia.",
                        sesion = resumen
                    }));
            return correcto ? 0 : 4;
        }
        catch (UnauthorizedAccessException)
        {
            await error.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        correcto = false,
                        detalle =
                            "Windows no ha concedido acceso al control multimedia global."
                    }));
            return 4;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        correcto = false,
                        detalle =
                            "No se pudo usar el control multimedia de Windows: "
                            + ex.Message
                    }));
            return 4;
        }
    }

    internal static bool TryAnalizar(
        IReadOnlyList<string> argumentos,
        out OpcionesComandoMultimedia? opciones,
        out string error)
    {
        opciones = null;

        if (argumentos.Count == 0)
        {
            error =
                "Uso: ControlPCIA.exe media list|status|play|pause|toggle|stop|next|previous|forward|rewind|seek|shuffle|repeat|rate|fullscreen|exit-fullscreen [--app aplicación].";
            return false;
        }

        string accion = argumentos[0].ToLowerInvariant();
        string aplicacion = string.Empty;
        double? valor = null;
        string modo = string.Empty;
        var restantes = new List<string>();

        for (int indice = 1;
             indice < argumentos.Count;
             indice++)
        {
            if (argumentos[indice].Equals(
                    "--app",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (indice + 1 >= argumentos.Count)
                {
                    error = "--app necesita un nombre.";
                    return false;
                }

                aplicacion = argumentos[++indice].Trim();
            }
            else
            {
                restantes.Add(argumentos[indice]);
            }
        }

        if (accion == "list")
        {
            if (restantes.Count > 0
                || aplicacion.Length > 0)
            {
                error = "media list no admite más argumentos.";
                return false;
            }

            opciones = new OpcionesComandoMultimedia(
                AccionComandoMultimedia.Listar);
            error = string.Empty;
            return true;
        }

        AccionComandoMultimedia? tipo = accion switch
        {
            "status" => AccionComandoMultimedia.Estado,
            "play" => AccionComandoMultimedia.Reproducir,
            "pause" => AccionComandoMultimedia.Pausar,
            "toggle" => AccionComandoMultimedia.Alternar,
            "stop" => AccionComandoMultimedia.Detener,
            "next" => AccionComandoMultimedia.Siguiente,
            "previous" => AccionComandoMultimedia.Anterior,
            "forward" => AccionComandoMultimedia.Avanzar,
            "rewind" => AccionComandoMultimedia.Retroceder,
            "seek" => AccionComandoMultimedia.Desplazar,
            "shuffle" => AccionComandoMultimedia.Aleatorio,
            "repeat" => AccionComandoMultimedia.Repeticion,
            "rate" => AccionComandoMultimedia.Velocidad,
            "fullscreen" =>
                AccionComandoMultimedia.PantallaCompleta,
            "exit-fullscreen" =>
                AccionComandoMultimedia.SalirPantallaCompleta,
            _ => null
        };

        if (tipo is null)
        {
            error =
                $"Acción multimedia no reconocida: {argumentos[0]}";
            return false;
        }

        switch (tipo)
        {
            case AccionComandoMultimedia.Desplazar:
            case AccionComandoMultimedia.Velocidad:
                if (restantes.Count != 1
                    || !double.TryParse(
                        restantes[0],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double numero))
                {
                    error = tipo
                            == AccionComandoMultimedia.Desplazar
                        ? "Uso: media seek <segundos relativos> [--app aplicación]."
                        : "Uso: media rate <velocidad> [--app aplicación].";
                    return false;
                }

                if (tipo
                    == AccionComandoMultimedia.Velocidad
                    && numero <= 0)
                {
                    error =
                        "La velocidad debe ser mayor que cero.";
                    return false;
                }

                valor = numero;
                break;

            case AccionComandoMultimedia.Aleatorio:
                if (restantes.Count != 1
                    || restantes[0].ToLowerInvariant()
                    is not ("on" or "off"))
                {
                    error =
                        "Uso: media shuffle on|off [--app aplicación].";
                    return false;
                }

                modo = restantes[0].ToLowerInvariant();
                break;

            case AccionComandoMultimedia.Repeticion:
                if (restantes.Count != 1
                    || restantes[0].ToLowerInvariant()
                    is not ("off" or "track" or "list"))
                {
                    error =
                        "Uso: media repeat off|track|list [--app aplicación].";
                    return false;
                }

                modo = restantes[0].ToLowerInvariant();
                break;

            default:
                if (restantes.Count > 0)
                {
                    error =
                        $"media {accion} no admite esos argumentos.";
                    return false;
                }

                break;
        }

        if (tipo is AccionComandoMultimedia.PantallaCompleta
                or AccionComandoMultimedia.SalirPantallaCompleta
            && aplicacion.Length > 0
            && !EsNombreNavegador(
                Normalizar(aplicacion)))
        {
            error =
                "La pantalla completa sólo admite --app browser o un navegador conocido.";
            return false;
        }

        opciones = new OpcionesComandoMultimedia(
            tipo.Value,
            aplicacion,
            valor,
            modo);
        error = string.Empty;
        return true;
    }

    private static async Task<int> EjecutarPantallaCompletaAsync(
        OpcionesComandoMultimedia opciones,
        TextWriter salida,
        TextWriter error)
    {
        string aplicacion =
            Normalizar(opciones.Aplicacion);

        if (aplicacion.Length > 0
            && !EsNombreNavegador(aplicacion))
        {
            await error.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        correcto = false,
                        detalle =
                            "La pantalla completa mediante F está disponible únicamente para navegadores."
                    }));
            return 4;
        }

        nint ventana =
            BuscarVentanaNavegador(
                aplicacion);

        if (ventana == nint.Zero)
        {
            await error.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        correcto = false,
                        detalle =
                            "No se encontró una ventana de navegador abierta."
                    }));
            return 4;
        }

        if (!ActivarVentana(ventana))
        {
            await error.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        correcto = false,
                        detalle =
                            "Windows no permitió activar la ventana del navegador."
                    }));
            return 4;
        }

        await Task.Delay(120);
        ushort tecla =
            opciones.Accion
            == AccionComandoMultimedia.PantallaCompleta
                ? VkF
                : VkEscape;
        bool enviado =
            EnviarTecla(tecla);
        TextWriter destino =
            enviado ? salida : error;
        await destino.WriteLineAsync(
            JsonSerializer.Serialize(
                new
                {
                    correcto = enviado,
                    detalle = enviado
                        ? opciones.Accion
                          == AccionComandoMultimedia
                              .PantallaCompleta
                            ? "Se envió al navegador la orden de poner el vídeo en pantalla completa."
                            : "Se envió al navegador la orden de salir de la pantalla completa."
                        : "Windows no aceptó el envío de la tecla al navegador."
                }));
        return enviado ? 0 : 4;
    }

    private static nint BuscarVentanaNavegador(
        string aplicacion)
    {
        nint primerPlano =
            GetForegroundWindow();

        if (EsVentanaNavegador(
                primerPlano,
                aplicacion))
        {
            return primerPlano;
        }

        foreach (string proceso in ObtenerProcesosNavegador(
                     aplicacion))
        {
            foreach (Process candidato in
                     Process.GetProcessesByName(proceso))
            {
                using (candidato)
                {
                    if (candidato.MainWindowHandle
                        != nint.Zero)
                    {
                        return candidato.MainWindowHandle;
                    }
                }
            }
        }

        return nint.Zero;
    }

    private static bool EsVentanaNavegador(
        nint ventana,
        string aplicacion)
    {
        if (ventana == nint.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(
            ventana,
            out uint idProceso);

        try
        {
            using Process proceso =
                Process.GetProcessById(
                    checked((int)idProceso));
            string nombre =
                Normalizar(proceso.ProcessName);
            return ObtenerProcesosNavegador(
                    aplicacion)
                .Contains(
                    nombre,
                    StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string>
        ObtenerProcesosNavegador(
            string aplicacion)
    {
        if (aplicacion is "edge" or "microsoftedge")
        {
            return ["msedge"];
        }

        if (aplicacion is "googlechrome")
        {
            return ["chrome"];
        }

        if (aplicacion.Length > 0
            && aplicacion != "browser")
        {
            return [aplicacion];
        }

        return
        [
            "msedge",
            "chrome",
            "firefox",
            "brave",
            "opera",
            "opera_gx",
            "vivaldi"
        ];
    }

    private static bool ActivarVentana(
        nint ventana)
    {
        if (IsIconic(ventana))
        {
            ShowWindowAsync(
                ventana,
                SwRestore);
        }

        nint primerPlano =
            GetForegroundWindow();
        uint hiloActual =
            GetCurrentThreadId();
        uint hiloPrimerPlano =
            primerPlano == nint.Zero
                ? 0
                : GetWindowThreadProcessId(
                    primerPlano,
                    out _);
        bool adjunto = false;

        try
        {
            if (hiloPrimerPlano != 0
                && hiloPrimerPlano != hiloActual)
            {
                adjunto =
                    AttachThreadInput(
                        hiloActual,
                        hiloPrimerPlano,
                        true);
            }

            BringWindowToTop(ventana);
            return SetForegroundWindow(ventana)
                   || GetForegroundWindow() == ventana;
        }
        finally
        {
            if (adjunto)
            {
                AttachThreadInput(
                    hiloActual,
                    hiloPrimerPlano,
                    false);
            }
        }
    }

    private static bool EnviarTecla(
        ushort tecla)
    {
        Input[] entradas =
        [
            new()
            {
                Tipo = InputKeyboard,
                Datos = new InputUnion
                {
                    Teclado = new KeyboardInput
                    {
                        TeclaVirtual = tecla
                    }
                }
            },
            new()
            {
                Tipo = InputKeyboard,
                Datos = new InputUnion
                {
                    Teclado = new KeyboardInput
                    {
                        TeclaVirtual = tecla,
                        Banderas = KeyEventKeyUp
                    }
                }
            }
        ];
        return SendInput(
                   checked((uint)entradas.Length),
                   entradas,
                   Marshal.SizeOf<Input>())
               == entradas.Length;
    }

    private static async Task<bool> EjecutarAccionAsync(
        GlobalSystemMediaTransportControlsSession sesion,
        OpcionesComandoMultimedia opciones)
    {
        switch (opciones.Accion)
        {
            case AccionComandoMultimedia.Reproducir:
                return await sesion.TryPlayAsync();
            case AccionComandoMultimedia.Pausar:
                return await sesion.TryPauseAsync();
            case AccionComandoMultimedia.Alternar:
                return await sesion.TryTogglePlayPauseAsync();
            case AccionComandoMultimedia.Detener:
                return await sesion.TryStopAsync();
            case AccionComandoMultimedia.Siguiente:
                return await sesion.TrySkipNextAsync();
            case AccionComandoMultimedia.Anterior:
                return await sesion.TrySkipPreviousAsync();
            case AccionComandoMultimedia.Avanzar:
                return await sesion.TryFastForwardAsync();
            case AccionComandoMultimedia.Retroceder:
                return await sesion.TryRewindAsync();
            case AccionComandoMultimedia.Desplazar:
                GlobalSystemMediaTransportControlsSessionTimelineProperties
                    tiempo = sesion.GetTimelineProperties();
                TimeSpan posicion =
                    tiempo.Position
                    + TimeSpan.FromSeconds(
                        opciones.Valor!.Value);
                posicion = posicion < tiempo.StartTime
                    ? tiempo.StartTime
                    : posicion > tiempo.EndTime
                        ? tiempo.EndTime
                        : posicion;
                return await sesion
                    .TryChangePlaybackPositionAsync(
                        posicion.Ticks);
            case AccionComandoMultimedia.Aleatorio:
                return await sesion
                    .TryChangeShuffleActiveAsync(
                        opciones.Modo == "on");
            case AccionComandoMultimedia.Repeticion:
                MediaPlaybackAutoRepeatMode repeticion =
                    opciones.Modo switch
                    {
                        "track" =>
                            MediaPlaybackAutoRepeatMode.Track,
                        "list" =>
                            MediaPlaybackAutoRepeatMode.List,
                        _ =>
                            MediaPlaybackAutoRepeatMode.None
                    };
                return await sesion
                    .TryChangeAutoRepeatModeAsync(
                        repeticion);
            case AccionComandoMultimedia.Velocidad:
                return await sesion
                    .TryChangePlaybackRateAsync(
                        opciones.Valor!.Value);
            default:
                return false;
        }
    }

    private static GlobalSystemMediaTransportControlsSession?
        SeleccionarSesion(
            GlobalSystemMediaTransportControlsSessionManager manager,
            IReadOnlyList<
                GlobalSystemMediaTransportControlsSession> sesiones,
            string aplicacion)
    {
        if (string.IsNullOrWhiteSpace(aplicacion))
        {
            return manager.GetCurrentSession()
                   ?? sesiones.FirstOrDefault(
                       EstaReproduciendo)
                   ?? sesiones.FirstOrDefault();
        }

        string objetivo =
            Normalizar(aplicacion);
        IEnumerable<
            GlobalSystemMediaTransportControlsSession> candidatas =
                sesiones.Where(
                    sesion =>
                    {
                        string origen =
                            Normalizar(
                                sesion.SourceAppUserModelId);
                        return objetivo == "browser"
                            ? EsNavegador(origen)
                            : origen.Contains(
                                objetivo,
                                StringComparison.Ordinal);
                    });
        return candidatas.FirstOrDefault(EstaReproduciendo)
               ?? candidatas.FirstOrDefault();
    }

    private static bool EstaReproduciendo(
        GlobalSystemMediaTransportControlsSession sesion)
    {
        return sesion.GetPlaybackInfo().PlaybackStatus
               == GlobalSystemMediaTransportControlsSessionPlaybackStatus
                   .Playing;
    }

    private static bool EsNavegador(
        string origen)
    {
        return new[]
            {
                "msedge",
                "chrome",
                "firefox",
                "brave",
                "opera",
                "vivaldi"
            }
            .Any(origen.Contains);
    }

    private static bool EsNombreNavegador(
        string nombre)
    {
        return nombre is
            "browser"
            or "edge"
            or "microsoftedge"
            or "msedge"
            or "googlechrome"
            or "chrome"
            or "firefox"
            or "brave"
            or "opera"
            or "opera_gx"
            or "vivaldi";
    }

    private static async Task<object> CrearResumenAsync(
        GlobalSystemMediaTransportControlsSession sesion)
    {
        GlobalSystemMediaTransportControlsSessionMediaProperties?
            propiedades = null;

        try
        {
            propiedades =
                await sesion.TryGetMediaPropertiesAsync();
        }
        catch
        {
            // Algunas aplicaciones publican controles, pero no metadatos.
        }

        GlobalSystemMediaTransportControlsSessionPlaybackInfo
            reproduccion = sesion.GetPlaybackInfo();
        GlobalSystemMediaTransportControlsSessionTimelineProperties
            tiempo = sesion.GetTimelineProperties();
        GlobalSystemMediaTransportControlsSessionPlaybackControls
            controles = reproduccion.Controls;
        return new
        {
            aplicacion = sesion.SourceAppUserModelId,
            titulo = propiedades?.Title ?? string.Empty,
            artista = propiedades?.Artist ?? string.Empty,
            album = propiedades?.AlbumTitle ?? string.Empty,
            estado =
                reproduccion.PlaybackStatus.ToString(),
            posicionSegundos =
                Math.Round(
                    tiempo.Position.TotalSeconds,
                    1),
            duracionSegundos =
                Math.Round(
                    (tiempo.EndTime - tiempo.StartTime)
                    .TotalSeconds,
                    1),
            permite = new
            {
                reproducir =
                    controles.IsPlayEnabled,
                pausar =
                    controles.IsPauseEnabled,
                alternar =
                    controles.IsPlayPauseToggleEnabled,
                detener =
                    controles.IsStopEnabled,
                siguiente =
                    controles.IsNextEnabled,
                anterior =
                    controles.IsPreviousEnabled,
                posicion =
                    controles.IsPlaybackPositionEnabled,
                velocidad =
                    controles.IsPlaybackRateEnabled,
                aleatorio =
                    controles.IsShuffleEnabled,
                repeticion =
                    controles.IsRepeatEnabled
            }
        };
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkF = 0x46;
    private const ushort VkEscape = 0x1B;
    private const int SwRestore = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Tipo;
        public InputUnion Datos;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Raton;

        [FieldOffset(0)]
        public KeyboardInput Teclado;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Datos;
        public uint Banderas;
        public uint Tiempo;
        public nuint InformacionExtra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort TeclaVirtual;
        public ushort CodigoExploracion;
        public uint Banderas;
        public uint Tiempo;
        public nuint InformacionExtra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Mensaje;
        public ushort ParametroBajo;
        public ushort ParametroAlto;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(
        uint cantidad,
        [In] Input[] entradas,
        int tamano);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint ventana,
        out uint procesoId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(
        nint ventana);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(
        nint ventana);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint hiloOrigen,
        uint hiloDestino,
        [MarshalAs(UnmanagedType.Bool)] bool adjuntar);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(
        nint ventana,
        int comando);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(
        nint ventana);

    private static string Normalizar(
        string texto)
    {
        return ControlBasico
            .Normalizar(texto)
            .Replace(
                " ",
                string.Empty,
                StringComparison.Ordinal);
    }
}
