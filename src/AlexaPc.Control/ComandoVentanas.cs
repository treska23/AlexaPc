using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ControlPCIA;

internal enum EstadoSolicitadoVentana
{
    Normal,
    Maximizada,
    Minimizada
}

internal sealed record OpcionesComandoVentana(
    bool Listar,
    string Coincidencia,
    EstadoSolicitadoVentana? Estado,
    bool PrimerPlano,
    bool Cerrar,
    int? X,
    int? Y,
    int? Ancho,
    int? Alto)
{
    public bool CambiaEstado =>
        Estado is not null
        || PrimerPlano
        || Cerrar
        || X is not null;
}

internal sealed record VentanaSuperior(
    nint Handle,
    int ProcesoId,
    string Proceso,
    string Titulo,
    string Estado,
    bool EnPrimerPlano,
    int X,
    int Y,
    int Ancho,
    int Alto);

/// <summary>
/// Superficie de consola genérica para consultar y controlar únicamente
/// ventanas superiores. No inspecciona contenido, no usa OCR, ratón,
/// teclado, SendKeys ni UI Automation.
/// </summary>
internal static class ComandoVentanas
{
    private const int SwShowMaximized = 3;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static async Task<int> EjecutarAsync(
        IReadOnlyList<string> argumentos,
        TextWriter salida,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (!TryAnalizar(
                argumentos,
                out OpcionesComandoVentana? opciones,
                out string errorArgumentos))
        {
            await error.WriteLineAsync(errorArgumentos);
            return 2;
        }

        OpcionesComandoVentana opcionesValidas = opciones!;
        IReadOnlyList<VentanaSuperior> coincidencias =
            BuscarVentanas(opcionesValidas.Coincidencia);

        if (!opcionesValidas.CambiaEstado)
        {
            await EscribirResultadoAsync(
                salida,
                opcionesValidas,
                coincidencias,
                correcto: true,
                detalle: coincidencias.Count == 0
                    ? "No hay ninguna ventana superior que coincida."
                    : "Consulta completada.");
            return 0;
        }

        VentanaSuperior? objetivo = coincidencias.FirstOrDefault();

        if (objetivo is null)
        {
            await EscribirResultadoAsync(
                error,
                opcionesValidas,
                coincidencias,
                correcto: false,
                detalle: "No hay ninguna ventana superior que coincida.");
            return 4;
        }

        bool correcto;
        string detalle;

        if (opcionesValidas.Cerrar)
        {
            correcto = PostMessage(
                objetivo.Handle,
                WmClose,
                0,
                0);
            detalle = correcto
                ? "Se solicitó a la aplicación que cierre la ventana."
                : "Windows no aceptó la solicitud de cierre.";

            if (correcto)
            {
                await EsperarCierreAsync(
                    objetivo.Handle,
                    cancellationToken);
                correcto = !IsWindow(objetivo.Handle);

                if (!correcto)
                {
                    detalle =
                        "La ventana sigue abierta. La aplicación puede estar esperando una decisión, por ejemplo guardar trabajo.";
                }
            }
        }
        else
        {
            correcto = AplicarCambios(
                objetivo.Handle,
                opcionesValidas);
            detalle = correcto
                ? "Cambios aplicados a la ventana superior."
                : "Windows no pudo aplicar todos los cambios solicitados.";
            await Task.Delay(120, cancellationToken);
        }

        IReadOnlyList<VentanaSuperior> comprobacion =
            BuscarVentanas(opcionesValidas.Coincidencia);
        await EscribirResultadoAsync(
            correcto ? salida : error,
            opcionesValidas,
            comprobacion,
            correcto,
            detalle);
        return correcto ? 0 : 4;
    }

    internal static bool TryAnalizar(
        IReadOnlyList<string> argumentos,
        out OpcionesComandoVentana? opciones,
        out string error)
    {
        bool listar = false;
        bool primerPlano = false;
        bool cerrar = false;
        string coincidencia = string.Empty;
        EstadoSolicitadoVentana? estado = null;
        int? x = null;
        int? y = null;
        int? ancho = null;
        int? alto = null;

        for (int indice = 0; indice < argumentos.Count; indice++)
        {
            string argumento = argumentos[indice];

            if (argumento.Equals(
                    "--list",
                    StringComparison.OrdinalIgnoreCase))
            {
                listar = true;
                continue;
            }

            if (argumento.Equals(
                    "--foreground",
                    StringComparison.OrdinalIgnoreCase))
            {
                primerPlano = true;
                continue;
            }

            if (argumento.Equals(
                    "--close",
                    StringComparison.OrdinalIgnoreCase))
            {
                cerrar = true;
                continue;
            }

            if (argumento.Equals(
                    "--match",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!TryObtenerValor(
                        argumentos,
                        ref indice,
                        "--match",
                        out coincidencia,
                        out error))
                {
                    opciones = null;
                    return false;
                }

                continue;
            }

            if (argumento.Equals(
                    "--state",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!TryObtenerValor(
                        argumentos,
                        ref indice,
                        "--state",
                        out string valorEstado,
                        out error)
                    || !TryAnalizarEstado(
                        valorEstado,
                        out estado))
                {
                    opciones = null;
                    error = error.Length == 0
                        ? "El estado debe ser normal, maximized o minimized."
                        : error;
                    return false;
                }

                continue;
            }

            if (TryAnalizarEntero(
                    argumentos,
                    ref indice,
                    argumento,
                    "--x",
                    ref x,
                    out error)
                || TryAnalizarEntero(
                    argumentos,
                    ref indice,
                    argumento,
                    "--y",
                    ref y,
                    out error)
                || TryAnalizarEntero(
                    argumentos,
                    ref indice,
                    argumento,
                    "--width",
                    ref ancho,
                    out error)
                || TryAnalizarEntero(
                    argumentos,
                    ref indice,
                    argumento,
                    "--height",
                    ref alto,
                    out error))
            {
                if (error.Length > 0)
                {
                    opciones = null;
                    return false;
                }

                continue;
            }

            opciones = null;
            error = $"Argumento de ventana no reconocido: {argumento}";
            return false;
        }

        if (cerrar
            && (estado is not null
                || primerPlano
                || x is not null
                || y is not null
                || ancho is not null
                || alto is not null))
        {
            opciones = null;
            error =
                "--close no se combina con cambios de estado o posición.";
            return false;
        }

        bool posicionParcial =
            new[] { x, y, ancho, alto }.Count(valor => valor is not null)
            is > 0 and < 4;

        if (posicionParcial)
        {
            opciones = null;
            error =
                "Para colocar una ventana indica --x, --y, --width y --height.";
            return false;
        }

        if (ancho is <= 0 || alto is <= 0)
        {
            opciones = null;
            error = "El ancho y el alto deben ser mayores que cero.";
            return false;
        }

        bool cambiaEstado =
            estado is not null
            || primerPlano
            || cerrar
            || x is not null;

        if (cambiaEstado && string.IsNullOrWhiteSpace(coincidencia))
        {
            opciones = null;
            error =
                "Una acción de ventana necesita --match con el proceso o título.";
            return false;
        }

        opciones = new OpcionesComandoVentana(
            listar || !cambiaEstado,
            coincidencia.Trim(),
            estado,
            primerPlano,
            cerrar,
            x,
            y,
            ancho,
            alto);
        error = string.Empty;
        return true;
    }

    private static bool TryAnalizarEntero(
        IReadOnlyList<string> argumentos,
        ref int indice,
        string argumento,
        string nombre,
        ref int? destino,
        out string error)
    {
        error = string.Empty;

        if (!argumento.Equals(
                nombre,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryObtenerValor(
                argumentos,
                ref indice,
                nombre,
                out string texto,
                out error)
            || !int.TryParse(
                texto,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int valor))
        {
            error = error.Length == 0
                ? $"{nombre} necesita un número entero."
                : error;
            return true;
        }

        destino = valor;
        return true;
    }

    private static bool TryObtenerValor(
        IReadOnlyList<string> argumentos,
        ref int indice,
        string nombre,
        out string valor,
        out string error)
    {
        if (indice + 1 >= argumentos.Count
            || argumentos[indice + 1].StartsWith(
                "--",
                StringComparison.Ordinal))
        {
            valor = string.Empty;
            error = $"{nombre} necesita un valor.";
            return false;
        }

        valor = argumentos[++indice];
        error = string.Empty;
        return true;
    }

    private static bool TryAnalizarEstado(
        string valor,
        out EstadoSolicitadoVentana? estado)
    {
        estado = valor.ToLowerInvariant() switch
        {
            "normal" or "restored" =>
                EstadoSolicitadoVentana.Normal,
            "maximized" or "maximizada" or "maximizado" =>
                EstadoSolicitadoVentana.Maximizada,
            "minimized" or "minimizada" or "minimizado" =>
                EstadoSolicitadoVentana.Minimizada,
            _ => null
        };

        return estado is not null;
    }

    private static IReadOnlyList<VentanaSuperior> BuscarVentanas(
        string coincidencia)
    {
        string filtro = Normalizar(coincidencia);
        nint primerPlano = GetForegroundWindow();
        var ventanas = new List<VentanaSuperior>();

        EnumWindows(
            (handle, _) =>
            {
                if (!IsWindowVisible(handle)
                    || GetWindowTextLength(handle) <= 0)
                {
                    return true;
                }

                GetWindowThreadProcessId(
                    handle,
                    out uint procesoId);

                string proceso;

                try
                {
                    proceso = Process
                        .GetProcessById(checked((int)procesoId))
                        .ProcessName;
                }
                catch
                {
                    return true;
                }

                string titulo = ObtenerTitulo(handle);

                if (filtro.Length > 0
                    && !Normalizar(proceso).Contains(
                        filtro,
                        StringComparison.Ordinal)
                    && !Normalizar(titulo).Contains(
                        filtro,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                if (!GetWindowRect(
                        handle,
                        out Rectangulo rectangulo))
                {
                    rectangulo = default;
                }

                string estado = IsIconic(handle)
                    ? "minimized"
                    : IsZoomed(handle)
                        ? "maximized"
                        : "normal";
                ventanas.Add(
                    new VentanaSuperior(
                        handle,
                        checked((int)procesoId),
                        proceso,
                        titulo,
                        estado,
                        handle == primerPlano,
                        rectangulo.Izquierda,
                        rectangulo.Arriba,
                        Math.Max(
                            0,
                            rectangulo.Derecha
                            - rectangulo.Izquierda),
                        Math.Max(
                            0,
                            rectangulo.Abajo
                            - rectangulo.Arriba)));
                return true;
            },
            0);

        return ventanas;
    }

    private static bool AplicarCambios(
        nint handle,
        OpcionesComandoVentana opciones)
    {
        bool correcto = true;

        if (opciones.Estado is not null)
        {
            int comando = opciones.Estado switch
            {
                EstadoSolicitadoVentana.Maximizada =>
                    SwShowMaximized,
                EstadoSolicitadoVentana.Minimizada =>
                    SwMinimize,
                _ =>
                    SwRestore
            };
            correcto &= ShowWindowAsync(handle, comando);
        }

        if (opciones.X is not null)
        {
            uint banderas = SwpNoZOrder;

            if (!opciones.PrimerPlano)
            {
                banderas |= SwpNoActivate;
            }

            correcto &= SetWindowPos(
                handle,
                0,
                opciones.X.Value,
                opciones.Y!.Value,
                opciones.Ancho!.Value,
                opciones.Alto!.Value,
                banderas);
        }

        if (opciones.PrimerPlano)
        {
            correcto &= ActivarVentana(handle);
        }

        return correcto;
    }

    private static bool ActivarVentana(nint handle)
    {
        if (IsIconic(handle))
        {
            ShowWindowAsync(handle, SwRestore);
        }

        nint primerPlano = GetForegroundWindow();
        uint hiloActual = GetCurrentThreadId();
        uint hiloPrimerPlano = primerPlano == 0
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
                adjunto = AttachThreadInput(
                    hiloActual,
                    hiloPrimerPlano,
                    true);
            }

            BringWindowToTop(handle);
            return SetForegroundWindow(handle)
                   || GetForegroundWindow() == handle;
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

    private static async Task EsperarCierreAsync(
        nint handle,
        CancellationToken cancellationToken)
    {
        for (int intento = 0;
             intento < 20 && IsWindow(handle);
             intento++)
        {
            await Task.Delay(100, cancellationToken);
        }
    }

    private static async Task EscribirResultadoAsync(
        TextWriter escritor,
        OpcionesComandoVentana opciones,
        IReadOnlyList<VentanaSuperior> ventanas,
        bool correcto,
        string detalle)
    {
        string json = JsonSerializer.Serialize(
            new
            {
                correcto,
                detalle,
                filtro = opciones.Coincidencia,
                coincidencias = ventanas.Count,
                ventanas = ventanas.Select(ventana => new
                {
                    proceso = ventana.Proceso,
                    pid = ventana.ProcesoId,
                    titulo = ventana.Titulo,
                    estado = ventana.Estado,
                    primerPlano = ventana.EnPrimerPlano,
                    x = ventana.X,
                    y = ventana.Y,
                    ancho = ventana.Ancho,
                    alto = ventana.Alto
                })
            });
        await escritor.WriteLineAsync(json);
    }

    private static string ObtenerTitulo(nint handle)
    {
        int longitud = GetWindowTextLength(handle);
        var titulo = new StringBuilder(longitud + 1);
        _ = GetWindowText(
            handle,
            titulo,
            titulo.Capacity);
        return titulo.ToString();
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
            .Normalize(NormalizationForm.FormC)
            .Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangulo
    {
        public int Izquierda;
        public int Arriba;
        public int Derecha;
        public int Abajo;
    }

    private delegate bool EnumeradorVentanas(
        nint ventana,
        nint parametro);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumeradorVentanas enumerador,
        nint parametro);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint ventana);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint ventana);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(nint ventana);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetWindowText(
        nint ventana,
        StringBuilder texto,
        int maximo);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint ventana,
        out uint procesoId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint ventana);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint ventana);

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
    private static extern bool SetWindowPos(
        nint ventana,
        nint insertarDespues,
        int x,
        int y,
        int ancho,
        int alto,
        uint banderas);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint ventana,
        uint mensaje,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint ventana);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint ventana);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint ventana,
        out Rectangulo rectangulo);
}
