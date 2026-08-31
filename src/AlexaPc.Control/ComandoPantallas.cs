using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace ControlPCIA;

internal enum AccionComandoPantalla
{
    Listar,
    ListarModos,
    EstablecerPrincipal,
    CambiarResolucion,
    CambiarFrecuencia,
    CambiarEscala,
    Activar,
    Desactivar,
    CambiarTopologia,
    CambiarOrientacion,
    CambiarPosicion,
    ColocarRelativa
}

internal enum TopologiaPantallas
{
    Extendida,
    Duplicada,
    SoloInterna,
    SoloExterna
}

internal enum OrientacionPantalla
{
    Horizontal = 0,
    Vertical = 1,
    HorizontalInvertida = 2,
    VerticalInvertida = 3
}

internal enum PosicionRelativaPantalla
{
    Izquierda,
    Derecha,
    Arriba,
    Abajo
}

internal sealed record OpcionesComandoPantalla(
    AccionComandoPantalla Accion,
    string Pantalla = "",
    int? Ancho = null,
    int? Alto = null,
    int? Frecuencia = null,
    int? X = null,
    int? Y = null,
    TopologiaPantallas? Topologia = null,
    OrientacionPantalla? Orientacion = null,
    PosicionRelativaPantalla? PosicionRelativa = null,
    string PantallaReferencia = "",
    int? Escala = null);

internal sealed record PantallaWindows(
    int Numero,
    string Dispositivo,
    string Adaptador,
    string Monitor,
    bool Activa,
    bool Principal,
    int X,
    int Y,
    int Ancho,
    int Alto,
    int Frecuencia,
    int Orientacion,
    int? Escala);

internal sealed record ModoPantalla(
    int Ancho,
    int Alto,
    int Frecuencia);

/// <summary>
/// Superficie de consola para la configuración de pantallas de Windows.
/// Utiliza exclusivamente las API Win32 de visualización; no abre la
/// aplicación Configuración ni simula ratón o teclado.
/// </summary>
internal static class ComandoPantallas
{
    private const int EnumCurrentSettings = -1;
    private const int EnumRegistrySettings = -2;

    private const int DisplayDeviceAttachedToDesktop = 0x00000001;
    private const int DisplayDevicePrimaryDevice = 0x00000004;
    private const int DisplayDeviceMirroringDriver = 0x00000008;

    private const int DmPosition = 0x00000020;
    private const int DmDisplayOrientation = 0x00000080;
    private const int DmPelsWidth = 0x00080000;
    private const int DmPelsHeight = 0x00100000;
    private const int DmDisplayFrequency = 0x00400000;

    private const uint CdsUpdateRegistry = 0x00000001;
    private const uint CdsTest = 0x00000002;
    private const uint CdsSetPrimary = 0x00000010;
    private const uint CdsNoReset = 0x10000000;

    private const int DispChangeSuccessful = 0;
    private const int DispChangeRestart = 1;

    private const uint SdcTopologyInternal = 0x00000001;
    private const uint SdcTopologyClone = 0x00000002;
    private const uint SdcTopologyExtend = 0x00000004;
    private const uint SdcTopologyExternal = 0x00000008;
    private const uint SdcApply = 0x00000080;
    private const uint SdcPathPersistIfRequired = 0x00000800;

    public static async Task<int> EjecutarAsync(
        IReadOnlyList<string> argumentos,
        TextWriter salida,
        TextWriter error)
    {
        if (!TryAnalizar(
                argumentos,
                out OpcionesComandoPantalla? opciones,
                out string errorArgumentos))
        {
            await error.WriteLineAsync(errorArgumentos);
            return 2;
        }

        try
        {
            ResultadoOperacion resultado =
                Ejecutar(opciones!);
            TextWriter destino =
                resultado.Correcto ? salida : error;
            await destino.WriteLineAsync(
                JsonSerializer.Serialize(
                    resultado.Contenido));
            return resultado.Correcto ? 0 : 4;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        correcto = false,
                        detalle =
                            "No se pudo consultar o configurar las pantallas: "
                            + ex.Message
                    }));
            return 4;
        }
    }

    internal static bool TryAnalizar(
        IReadOnlyList<string> argumentos,
        out OpcionesComandoPantalla? opciones,
        out string error)
    {
        opciones = null;

        if (argumentos.Count == 0)
        {
            error =
                "Uso: ControlPCIA.exe display list|modes|primary|resolution|frequency|scale|enable|disable|topology|orientation|position|place.";
            return false;
        }

        string accion = argumentos[0].ToLowerInvariant();

        switch (accion)
        {
            case "list":
                if (argumentos.Count != 1)
                {
                    return Fallar(
                        "display list no admite más argumentos.",
                        out error);
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.Listar);
                error = string.Empty;
                return true;

            case "modes":
                if (!TryCantidad(
                        argumentos,
                        2,
                        "Uso: display modes <pantalla>.",
                        out error))
                {
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.ListarModos,
                    argumentos[1]);
                return true;

            case "primary":
                if (!TryCantidad(
                        argumentos,
                        2,
                        "Uso: display primary <pantalla>.",
                        out error))
                {
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.EstablecerPrincipal,
                    argumentos[1]);
                return true;

            case "resolution":
                if (argumentos.Count is not (4 or 5)
                    || !TryEnteroPositivo(
                        argumentos[2],
                        out int ancho)
                    || !TryEnteroPositivo(
                        argumentos[3],
                        out int alto)
                    || (argumentos.Count == 5
                        && !TryEnteroPositivo(
                            argumentos[4],
                            out _)))
                {
                    error =
                        "Uso: display resolution <pantalla> <ancho> <alto> [Hz].";
                    return false;
                }

                int? frecuenciaResolucion =
                    argumentos.Count == 5
                        ? int.Parse(
                            argumentos[4],
                            CultureInfo.InvariantCulture)
                        : null;
                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.CambiarResolucion,
                    argumentos[1],
                    ancho,
                    alto,
                    frecuenciaResolucion);
                error = string.Empty;
                return true;

            case "frequency":
                if (argumentos.Count != 3
                    || !TryEnteroPositivo(
                        argumentos[2],
                        out int frecuencia))
                {
                    error =
                        "Uso: display frequency <pantalla> <Hz>.";
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.CambiarFrecuencia,
                    argumentos[1],
                    Frecuencia: frecuencia);
                error = string.Empty;
                return true;

            case "scale":
                if (argumentos.Count != 3
                    || !TryEnteroPositivo(
                        argumentos[2],
                        out int escala)
                    || !EscalasDpi.Contains(escala))
                {
                    error =
                        "Uso: display scale <pantalla> <porcentaje>.";
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.CambiarEscala,
                    argumentos[1],
                    Escala: escala);
                error = string.Empty;
                return true;

            case "enable":
            case "disable":
                if (!TryCantidad(
                        argumentos,
                        2,
                        $"Uso: display {accion} <pantalla>.",
                        out error))
                {
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    accion == "enable"
                        ? AccionComandoPantalla.Activar
                        : AccionComandoPantalla.Desactivar,
                    argumentos[1]);
                return true;

            case "topology":
                if (!TryCantidad(
                        argumentos,
                        2,
                        "Uso: display topology extend|clone|internal|external.",
                        out error)
                    || !TryTopologia(
                        argumentos[1],
                        out TopologiaPantallas topologia))
                {
                    error = error.Length == 0
                        ? "La topología debe ser extend, clone, internal o external."
                        : error;
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.CambiarTopologia,
                    Topologia: topologia);
                return true;

            case "orientation":
                if (argumentos.Count != 3
                    || !TryOrientacion(
                        argumentos[2],
                        out OrientacionPantalla orientacion))
                {
                    error =
                        "Uso: display orientation <pantalla> landscape|portrait|landscape-flipped|portrait-flipped.";
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.CambiarOrientacion,
                    argumentos[1],
                    Orientacion: orientacion);
                error = string.Empty;
                return true;

            case "position":
                if (argumentos.Count != 4
                    || !TryEntero(
                        argumentos[2],
                        out int x)
                    || !TryEntero(
                        argumentos[3],
                        out int y))
                {
                    error =
                        "Uso: display position <pantalla> <x> <y>.";
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.CambiarPosicion,
                    argumentos[1],
                    X: x,
                    Y: y);
                error = string.Empty;
                return true;

            case "place":
                if (argumentos.Count != 4
                    || !TryPosicionRelativa(
                        argumentos[2],
                        out PosicionRelativaPantalla posicion))
                {
                    error =
                        "Uso: display place <pantalla> left|right|above|below <referencia>.";
                    return false;
                }

                opciones = new OpcionesComandoPantalla(
                    AccionComandoPantalla.ColocarRelativa,
                    argumentos[1],
                    PosicionRelativa: posicion,
                    PantallaReferencia: argumentos[3]);
                error = string.Empty;
                return true;
        }

        error = $"Acción de pantalla no reconocida: {argumentos[0]}";
        return false;
    }

    private static ResultadoOperacion Ejecutar(
        OpcionesComandoPantalla opciones)
    {
        IReadOnlyList<PantallaWindows> pantallas =
            ObtenerPantallas();

        if (opciones.Accion == AccionComandoPantalla.Listar)
        {
            return Correcto(
                "Consulta de pantallas completada.",
                new
                {
                    correcto = true,
                    detalle = "Consulta de pantallas completada.",
                    pantallas
                });
        }

        if (opciones.Accion
            == AccionComandoPantalla.CambiarTopologia)
        {
            int codigo = SetDisplayConfig(
                0,
                nint.Zero,
                0,
                nint.Zero,
                SdcApply
                | SdcPathPersistIfRequired
                | ObtenerBanderaTopologia(
                    opciones.Topologia!.Value));
            return ResultadoCodigoWindows(
                codigo,
                codigo == 0,
                codigo == 0
                    ? "Windows aceptó el cambio de topología."
                    : "Windows rechazó el cambio de topología.");
        }

        PantallaWindows? pantalla =
            ResolverPantalla(
                opciones.Pantalla,
                pantallas);

        if (pantalla is null)
        {
            return Incorrecto(
                $"No existe la pantalla «{opciones.Pantalla}». Usa display list para consultar sus números.");
        }

        if (opciones.Accion == AccionComandoPantalla.ListarModos)
        {
            IReadOnlyList<ModoPantalla> modos =
                ObtenerModos(
                    pantalla.Dispositivo);
            return Correcto(
                "Consulta de modos completada.",
                new
                {
                    correcto = true,
                    detalle = "Consulta de modos completada.",
                    pantalla = pantalla.Numero,
                    dispositivo = pantalla.Dispositivo,
                    modos
                });
        }

        if (opciones.Accion
            == AccionComandoPantalla.EstablecerPrincipal)
        {
            return EstablecerPrincipal(
                pantalla,
                pantallas);
        }

        if (opciones.Accion
            == AccionComandoPantalla.Activar)
        {
            return Activar(
                pantalla,
                pantallas);
        }

        if (opciones.Accion
            == AccionComandoPantalla.Desactivar)
        {
            return Desactivar(
                pantalla,
                pantallas);
        }

        if (!pantalla.Activa)
        {
            return Incorrecto(
                $"La pantalla {pantalla.Numero} está desactivada. Actívala antes de cambiar su configuración.");
        }

        if (!TryObtenerModo(
                pantalla.Dispositivo,
                EnumCurrentSettings,
                out DevMode modo))
        {
            return Incorrecto(
                $"Windows no ha devuelto la configuración actual de la pantalla {pantalla.Numero}.");
        }

        switch (opciones.Accion)
        {
            case AccionComandoPantalla.CambiarResolucion:
                return CambiarResolucion(
                    pantalla,
                    modo,
                    opciones);

            case AccionComandoPantalla.CambiarFrecuencia:
                return CambiarFrecuencia(
                    pantalla,
                    modo,
                    opciones.Frecuencia!.Value);

            case AccionComandoPantalla.CambiarEscala:
                return CambiarEscala(
                    pantalla,
                    opciones.Escala!.Value);

            case AccionComandoPantalla.CambiarOrientacion:
                return CambiarOrientacion(
                    pantalla,
                    modo,
                    opciones.Orientacion!.Value);

            case AccionComandoPantalla.CambiarPosicion:
                modo.DmPositionX = opciones.X!.Value;
                modo.DmPositionY = opciones.Y!.Value;
                modo.DmFields = DmPosition;
                return ProbarYAplicar(
                    pantalla,
                    ref modo,
                    CdsUpdateRegistry,
                    "Windows aceptó la nueva posición de la pantalla.");

            case AccionComandoPantalla.ColocarRelativa:
                PantallaWindows? referencia =
                    ResolverPantalla(
                        opciones.PantallaReferencia,
                        pantallas);

                if (referencia is null || !referencia.Activa)
                {
                    return Incorrecto(
                        $"La pantalla de referencia «{opciones.PantallaReferencia}» no existe o no está activa.");
                }

                (modo.DmPositionX, modo.DmPositionY) =
                    CalcularPosicionRelativa(
                        pantalla,
                        referencia,
                        opciones.PosicionRelativa!.Value);
                modo.DmFields = DmPosition;
                return ProbarYAplicar(
                    pantalla,
                    ref modo,
                    CdsUpdateRegistry,
                    "Windows aceptó la nueva colocación de la pantalla.");

            default:
                return Incorrecto(
                    "La acción de pantalla no se pudo ejecutar.");
        }
    }

    private static ResultadoOperacion CambiarResolucion(
        PantallaWindows pantalla,
        DevMode modo,
        OpcionesComandoPantalla opciones)
    {
        int ancho = opciones.Ancho!.Value;
        int alto = opciones.Alto!.Value;
        int frecuencia =
            opciones.Frecuencia ?? modo.DmDisplayFrequency;
        bool compatible =
            ObtenerModos(pantalla.Dispositivo)
                .Any(candidato =>
                    candidato.Ancho == ancho
                    && candidato.Alto == alto
                    && (opciones.Frecuencia is null
                        || candidato.Frecuencia == frecuencia));

        if (!compatible)
        {
            return Incorrecto(
                $"La pantalla {pantalla.Numero} no anuncia el modo {ancho}x{alto}"
                + (opciones.Frecuencia is null
                    ? "."
                    : $" a {frecuencia} Hz."));
        }

        modo.DmPelsWidth = ancho;
        modo.DmPelsHeight = alto;
        modo.DmFields = DmPelsWidth | DmPelsHeight;

        if (opciones.Frecuencia is not null)
        {
            modo.DmDisplayFrequency = frecuencia;
            modo.DmFields |= DmDisplayFrequency;
        }

        return ProbarYAplicar(
            pantalla,
            ref modo,
            CdsUpdateRegistry,
            "Windows aceptó la nueva resolución de la pantalla.");
    }

    private static ResultadoOperacion CambiarFrecuencia(
        PantallaWindows pantalla,
        DevMode modo,
        int frecuencia)
    {
        bool compatible =
            ObtenerModos(pantalla.Dispositivo)
                .Any(candidato =>
                    candidato.Ancho == modo.DmPelsWidth
                    && candidato.Alto == modo.DmPelsHeight
                    && candidato.Frecuencia == frecuencia);

        if (!compatible)
        {
            return Incorrecto(
                $"La pantalla {pantalla.Numero} no anuncia {frecuencia} Hz para su resolución actual.");
        }

        modo.DmDisplayFrequency = frecuencia;
        modo.DmFields = DmDisplayFrequency;
        return ProbarYAplicar(
            pantalla,
            ref modo,
            CdsUpdateRegistry,
            "Windows aceptó la nueva frecuencia de la pantalla.");
    }

    private static ResultadoOperacion CambiarEscala(
        PantallaWindows pantalla,
        int porcentaje)
    {
        int indiceEscala =
            Array.IndexOf(
                EscalasDpi,
                porcentaje);

        if (indiceEscala < 0)
        {
            return Incorrecto(
                "La escala debe ser 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450 o 500 por ciento.");
        }

        if (!TryObtenerOrigenDisplayConfig(
                pantalla.Dispositivo,
                out DISPLAYCONFIG_PATH_SOURCE_INFO origen,
                out int codigoOrigen))
        {
            return ResultadoCodigoWindows(
                codigoOrigen,
                false,
                $"Windows no pudo localizar la pantalla {pantalla.Numero} para cambiar su escala.");
        }

        DisplayConfigSourceDpiScaleGet consulta =
            new()
            {
                Header = new DisplayConfigDeviceInfoHeaderDpi
                {
                    Type = DisplayConfigDeviceInfoGetDpiScale,
                    Size = checked((uint)Marshal.SizeOf<
                        DisplayConfigSourceDpiScaleGet>()),
                    AdapterId = origen.adapterId,
                    Id = origen.id
                }
            };
        int codigoConsulta =
            DisplayConfigGetDeviceInfoDpi(
                ref consulta);

        if (codigoConsulta != 0)
        {
            return ResultadoCodigoWindows(
                codigoConsulta,
                false,
                $"Windows no pudo consultar las escalas admitidas por la pantalla {pantalla.Numero}.");
        }

        int indiceRecomendado =
            -consulta.MinScaleRel;
        int valorRelativo =
            indiceEscala
            - indiceRecomendado;

        if (valorRelativo < consulta.MinScaleRel
            || valorRelativo > consulta.MaxScaleRel)
        {
            int minimo =
                EscalaDesdeIndiceRelativo(
                    indiceRecomendado,
                    consulta.MinScaleRel);
            int maximo =
                EscalaDesdeIndiceRelativo(
                    indiceRecomendado,
                    consulta.MaxScaleRel);
            return Incorrecto(
                $"La pantalla {pantalla.Numero} admite una escala entre {minimo}% y {maximo}%.");
        }

        DisplayConfigSourceDpiScaleSet cambio =
            new()
            {
                Header = new DisplayConfigDeviceInfoHeaderDpi
                {
                    Type = DisplayConfigDeviceInfoSetDpiScale,
                    Size = checked((uint)Marshal.SizeOf<
                        DisplayConfigSourceDpiScaleSet>()),
                    AdapterId = origen.adapterId,
                    Id = origen.id
                },
                ScaleRel = valorRelativo
            };
        int codigoCambio =
            DisplayConfigSetDeviceInfoDpi(
                ref cambio);
        return ResultadoCodigoWindows(
            codigoCambio,
            codigoCambio == 0,
            codigoCambio == 0
                ? $"Windows aceptó la escala del {porcentaje}% para la pantalla {pantalla.Numero}."
                : $"Windows rechazó la escala del {porcentaje}% para la pantalla {pantalla.Numero}.");
    }

    private static int? ObtenerEscalaActual(
        string dispositivo)
    {
        if (!TryObtenerOrigenDisplayConfig(
                dispositivo,
                out DISPLAYCONFIG_PATH_SOURCE_INFO origen,
                out _))
        {
            return null;
        }

        DisplayConfigSourceDpiScaleGet consulta =
            new()
            {
                Header = new DisplayConfigDeviceInfoHeaderDpi
                {
                    Type = DisplayConfigDeviceInfoGetDpiScale,
                    Size = checked((uint)Marshal.SizeOf<
                        DisplayConfigSourceDpiScaleGet>()),
                    AdapterId = origen.adapterId,
                    Id = origen.id
                }
            };

        if (DisplayConfigGetDeviceInfoDpi(
                ref consulta) != 0)
        {
            return null;
        }

        int indice =
            -consulta.MinScaleRel
            + consulta.CurScaleRel;
        return indice >= 0
               && indice < EscalasDpi.Length
            ? EscalasDpi[indice]
            : null;
    }

    private static int EscalaDesdeIndiceRelativo(
        int indiceRecomendado,
        int relativo)
    {
        int indice =
            Math.Clamp(
                indiceRecomendado + relativo,
                0,
                EscalasDpi.Length - 1);
        return EscalasDpi[indice];
    }

    private static ResultadoOperacion CambiarOrientacion(
        PantallaWindows pantalla,
        DevMode modo,
        OrientacionPantalla orientacion)
    {
        int anterior = modo.DmDisplayOrientation;
        int nueva = (int)orientacion;

        if (anterior % 2 != nueva % 2)
        {
            (modo.DmPelsWidth, modo.DmPelsHeight) =
                (modo.DmPelsHeight, modo.DmPelsWidth);
            modo.DmFields =
                DmDisplayOrientation
                | DmPelsWidth
                | DmPelsHeight;
        }
        else
        {
            modo.DmFields = DmDisplayOrientation;
        }

        modo.DmDisplayOrientation = nueva;
        return ProbarYAplicar(
            pantalla,
            ref modo,
            CdsUpdateRegistry,
            "Windows aceptó la nueva orientación de la pantalla.");
    }

    private static ResultadoOperacion EstablecerPrincipal(
        PantallaWindows objetivo,
        IReadOnlyList<PantallaWindows> pantallas)
    {
        if (!objetivo.Activa)
        {
            return Incorrecto(
                "No se puede convertir en principal una pantalla desactivada.");
        }

        if (objetivo.Principal)
        {
            return Correcto(
                $"La pantalla {objetivo.Numero} ya es la principal.");
        }

        const QUERY_DISPLAY_CONFIG_FLAGS banderasConsulta =
            QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
        DISPLAYCONFIG_PATH_INFO[] rutas = [];
        DISPLAYCONFIG_MODE_INFO[] modos = [];
        uint cantidadRutas = 0;
        uint cantidadModos = 0;
        WIN32_ERROR codigoConsulta =
            WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER;

        for (int intento = 0;
             intento < 3
             && codigoConsulta
             == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER;
             intento++)
        {
            WIN32_ERROR codigoTamano =
                PInvoke.GetDisplayConfigBufferSizes(
                    banderasConsulta,
                    out cantidadRutas,
                    out cantidadModos);

            if (codigoTamano != WIN32_ERROR.ERROR_SUCCESS)
            {
                return ResultadoCodigoWindows(
                    unchecked((int)(uint)codigoTamano),
                    false,
                    "Windows no pudo calcular la configuración activa de las pantallas.");
            }

            rutas =
                new DISPLAYCONFIG_PATH_INFO[cantidadRutas];
            modos =
                new DISPLAYCONFIG_MODE_INFO[cantidadModos];
            codigoConsulta =
                PInvoke.QueryDisplayConfig(
                    banderasConsulta,
                    ref cantidadRutas,
                    rutas,
                    ref cantidadModos,
                    modos);
        }

        if (codigoConsulta != WIN32_ERROR.ERROR_SUCCESS)
        {
            return ResultadoCodigoWindows(
                unchecked((int)(uint)codigoConsulta),
                false,
                "Windows no pudo leer la configuración activa de las pantallas.");
        }

        Array.Resize(
            ref rutas,
            checked((int)cantidadRutas));
        Array.Resize(
            ref modos,
            checked((int)cantidadModos));

        int indiceObjetivo = -1;

        for (int indice = 0;
             indice < rutas.Length;
             indice++)
        {
            string? dispositivo =
                ObtenerNombreDispositivoGdi(
                    rutas[indice].sourceInfo);

            if (string.Equals(
                    dispositivo,
                    objetivo.Dispositivo,
                    StringComparison.OrdinalIgnoreCase))
            {
                indiceObjetivo = indice;
                break;
            }
        }

        if (indiceObjetivo < 0)
        {
            return Incorrecto(
                $"Windows no encontró la pantalla {objetivo.Numero} dentro de la topología activa.");
        }

        uint indiceModoObjetivo =
            rutas[indiceObjetivo].sourceInfo.modeInfoIdx;

        if (indiceModoObjetivo >= modos.Length
            || modos[indiceModoObjetivo].infoType
            != DISPLAYCONFIG_MODE_INFO_TYPE
                .DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            return Incorrecto(
                $"Windows no devolvió una posición válida para la pantalla {objetivo.Numero}.");
        }

        int desplazamientoX =
            modos[indiceModoObjetivo]
                .sourceMode
                .position
                .x;
        int desplazamientoY =
            modos[indiceModoObjetivo]
                .sourceMode
                .position
                .y;

        for (int indice = 0;
             indice < modos.Length;
             indice++)
        {
            if (modos[indice].infoType
                != DISPLAYCONFIG_MODE_INFO_TYPE
                    .DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                continue;
            }

            modos[indice].sourceMode.position.x -=
                desplazamientoX;
            modos[indice].sourceMode.position.y -=
                desplazamientoY;
        }

        if (indiceObjetivo != 0)
        {
            DISPLAYCONFIG_PATH_INFO rutaObjetivo =
                rutas[indiceObjetivo];
            Array.Copy(
                rutas,
                0,
                rutas,
                1,
                indiceObjetivo);
            rutas[0] = rutaObjetivo;
        }

        const SET_DISPLAY_CONFIG_FLAGS banderasAplicacion =
            SET_DISPLAY_CONFIG_FLAGS.SDC_APPLY
            | SET_DISPLAY_CONFIG_FLAGS.SDC_ALLOW_CHANGES
            | SET_DISPLAY_CONFIG_FLAGS
                .SDC_USE_SUPPLIED_DISPLAY_CONFIG
            | SET_DISPLAY_CONFIG_FLAGS.SDC_SAVE_TO_DATABASE;
        int codigoAplicacion =
            PInvoke.SetDisplayConfig(
                rutas,
                modos,
                banderasAplicacion);

        if (codigoAplicacion != 0)
        {
            return ResultadoCodigoWindows(
                codigoAplicacion,
                false,
                $"Windows rechazó convertir la pantalla {objetivo.Numero} en principal.");
        }

        PantallaWindows? resultado =
            ObtenerPantallas()
                .FirstOrDefault(
                    pantalla =>
                        pantalla.Dispositivo.Equals(
                            objetivo.Dispositivo,
                            StringComparison.OrdinalIgnoreCase));

        if (resultado?.Principal != true)
        {
            return Incorrecto(
                $"Windows aceptó la configuración, pero la pantalla {objetivo.Numero} no quedó marcada como principal.");
        }

        return Correcto(
            $"La pantalla {objetivo.Numero} es ahora la principal.");
    }

    private static string? ObtenerNombreDispositivoGdi(
        DISPLAYCONFIG_PATH_SOURCE_INFO origen)
    {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME nombre =
            default;
        nombre.header.type =
            DISPLAYCONFIG_DEVICE_INFO_TYPE
                .DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        nombre.header.size =
            checked((uint)Marshal.SizeOf<
                DISPLAYCONFIG_SOURCE_DEVICE_NAME>());
        nombre.header.adapterId = origen.adapterId;
        nombre.header.id = origen.id;
        int codigo =
            PInvoke.DisplayConfigGetDeviceInfo(
                ref nombre.header);
        return codigo == 0
            ? nombre.viewGdiDeviceName.ToString()
            : null;
    }

    private static bool TryObtenerOrigenDisplayConfig(
        string dispositivo,
        out DISPLAYCONFIG_PATH_SOURCE_INFO origen,
        out int codigo)
    {
        const QUERY_DISPLAY_CONFIG_FLAGS banderas =
            QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;
        origen = default;
        codigo = 0;

        for (int intento = 0;
             intento < 3;
             intento++)
        {
            WIN32_ERROR codigoTamano =
                PInvoke.GetDisplayConfigBufferSizes(
                    banderas,
                    out uint cantidadRutas,
                    out uint cantidadModos);

            if (codigoTamano != WIN32_ERROR.ERROR_SUCCESS)
            {
                codigo =
                    unchecked((int)(uint)codigoTamano);
                return false;
            }

            var rutas =
                new DISPLAYCONFIG_PATH_INFO[cantidadRutas];
            var modos =
                new DISPLAYCONFIG_MODE_INFO[cantidadModos];
            WIN32_ERROR codigoConsulta =
                PInvoke.QueryDisplayConfig(
                    banderas,
                    ref cantidadRutas,
                    rutas,
                    ref cantidadModos,
                    modos);

            if (codigoConsulta
                == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }

            if (codigoConsulta != WIN32_ERROR.ERROR_SUCCESS)
            {
                codigo =
                    unchecked((int)(uint)codigoConsulta);
                return false;
            }

            for (int indice = 0;
                 indice < cantidadRutas;
                 indice++)
            {
                if (string.Equals(
                        ObtenerNombreDispositivoGdi(
                            rutas[indice].sourceInfo),
                        dispositivo,
                        StringComparison.OrdinalIgnoreCase))
                {
                    origen =
                        rutas[indice].sourceInfo;
                    return true;
                }
            }

            codigo = 1168;
            return false;
        }

        codigo =
            unchecked((int)(uint)
                WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER);
        return false;
    }

    private static ResultadoOperacion Desactivar(
        PantallaWindows pantalla,
        IReadOnlyList<PantallaWindows> pantallas)
    {
        if (!pantalla.Activa)
        {
            return Correcto(
                $"La pantalla {pantalla.Numero} ya está desactivada.");
        }

        if (pantallas.Count(candidata => candidata.Activa) <= 1)
        {
            return Incorrecto(
                "Windows necesita conservar al menos una pantalla activa.");
        }

        if (pantalla.Principal)
        {
            PantallaWindows nuevaPrincipal =
                pantallas.First(candidata =>
                    candidata.Activa
                    && candidata.Numero != pantalla.Numero);
            ResultadoOperacion cambioPrincipal =
                EstablecerPrincipal(
                    nuevaPrincipal,
                    pantallas);

            if (!cambioPrincipal.Correcto)
            {
                return cambioPrincipal;
            }
        }

        if (!TryObtenerModo(
                pantalla.Dispositivo,
                EnumCurrentSettings,
                out DevMode modo))
        {
            return Incorrecto(
                $"No se pudo leer la pantalla {pantalla.Numero}.");
        }

        modo.DmPelsWidth = 0;
        modo.DmPelsHeight = 0;
        modo.DmFields = DmPosition;
        int preparar = ChangeDisplaySettingsEx(
            pantalla.Dispositivo,
            ref modo,
            nint.Zero,
            CdsUpdateRegistry | CdsNoReset,
            nint.Zero);

        if (!CodigoAceptado(preparar))
        {
            return ResultadoCambioPantalla(
                preparar,
                false,
                $"Windows rechazó la desactivación de la pantalla {pantalla.Numero}.");
        }

        int aplicar = ChangeDisplaySettingsEx(
            null,
            nint.Zero,
            nint.Zero,
            0,
            nint.Zero);
        return ResultadoCambioPantalla(
            aplicar,
            CodigoAceptado(aplicar),
            $"Windows aceptó la desactivación de la pantalla {pantalla.Numero}.");
    }

    private static ResultadoOperacion Activar(
        PantallaWindows pantalla,
        IReadOnlyList<PantallaWindows> pantallas)
    {
        if (pantalla.Activa)
        {
            return Correcto(
                $"La pantalla {pantalla.Numero} ya está activa.");
        }

        if (!TryObtenerModo(
                pantalla.Dispositivo,
                EnumRegistrySettings,
                out DevMode modo))
        {
            return Incorrecto(
                $"Windows no conserva un modo para activar la pantalla {pantalla.Numero}.");
        }

        int bordeDerecho = pantallas
            .Where(candidata => candidata.Activa)
            .Select(candidata => candidata.X + candidata.Ancho)
            .DefaultIfEmpty(0)
            .Max();
        modo.DmPositionX = bordeDerecho;
        modo.DmPositionY = pantallas
            .Where(candidata => candidata.Activa)
            .Select(candidata => candidata.Y)
            .DefaultIfEmpty(0)
            .Min();
        modo.DmFields = DmPosition;
        int preparar = ChangeDisplaySettingsEx(
            pantalla.Dispositivo,
            ref modo,
            nint.Zero,
            CdsUpdateRegistry | CdsNoReset,
            nint.Zero);

        if (!CodigoAceptado(preparar))
        {
            return ResultadoCambioPantalla(
                preparar,
                false,
                $"Windows rechazó la activación de la pantalla {pantalla.Numero}.");
        }

        int aplicar = ChangeDisplaySettingsEx(
            null,
            nint.Zero,
            nint.Zero,
            0,
            nint.Zero);
        return ResultadoCambioPantalla(
            aplicar,
            CodigoAceptado(aplicar),
            $"Windows aceptó la activación de la pantalla {pantalla.Numero}.");
    }

    private static ResultadoOperacion ProbarYAplicar(
        PantallaWindows pantalla,
        ref DevMode modo,
        uint banderas,
        string detalleCorrecto)
    {
        int prueba = ChangeDisplaySettingsEx(
            pantalla.Dispositivo,
            ref modo,
            nint.Zero,
            CdsTest,
            nint.Zero);

        if (!CodigoAceptado(prueba))
        {
            return ResultadoCambioPantalla(
                prueba,
                false,
                "Windows indica que ese modo de pantalla no es compatible.");
        }

        int codigo = ChangeDisplaySettingsEx(
            pantalla.Dispositivo,
            ref modo,
            nint.Zero,
            banderas,
            nint.Zero);
        return ResultadoCambioPantalla(
            codigo,
            CodigoAceptado(codigo),
            detalleCorrecto);
    }

    internal static IReadOnlyList<PantallaWindows> ObtenerPantallas()
    {
        var pantallas = new List<PantallaWindows>();

        for (uint indice = 0; ; indice++)
        {
            DisplayDevice adaptador = CrearDisplayDevice();

            if (!EnumDisplayDevices(
                    null,
                    indice,
                    ref adaptador,
                    0))
            {
                break;
            }

            if ((adaptador.StateFlags
                 & DisplayDeviceMirroringDriver) != 0)
            {
                continue;
            }

            bool activa =
                (adaptador.StateFlags
                 & DisplayDeviceAttachedToDesktop) != 0;
            bool tieneModoActual = TryObtenerModo(
                adaptador.DeviceName,
                EnumCurrentSettings,
                out DevMode modoActual);
            bool tieneModoRegistrado = TryObtenerModo(
                adaptador.DeviceName,
                EnumRegistrySettings,
                out DevMode modoRegistrado);

            if (!activa
                && !tieneModoActual
                && !tieneModoRegistrado)
            {
                continue;
            }

            DevMode modo = tieneModoActual
                ? modoActual
                : modoRegistrado;
            DisplayDevice monitor = CrearDisplayDevice();
            bool tieneMonitor = EnumDisplayDevices(
                adaptador.DeviceName,
                0,
                ref monitor,
                0);
            pantallas.Add(
                new PantallaWindows(
                    pantallas.Count + 1,
                    adaptador.DeviceName,
                    adaptador.DeviceString,
                    tieneMonitor
                        ? monitor.DeviceString
                        : adaptador.DeviceString,
                    activa,
                    (adaptador.StateFlags
                     & DisplayDevicePrimaryDevice) != 0,
                    modo.DmPositionX,
                    modo.DmPositionY,
                    modo.DmPelsWidth,
                    modo.DmPelsHeight,
                    modo.DmDisplayFrequency,
                    modo.DmDisplayOrientation,
                    activa
                        ? ObtenerEscalaActual(
                            adaptador.DeviceName)
                        : null));
        }

        return pantallas;
    }

    internal static IReadOnlyList<ModoPantalla> ObtenerModos(
        string dispositivo)
    {
        var modos = new HashSet<ModoPantalla>();

        for (int indice = 0; ; indice++)
        {
            if (!TryObtenerModo(
                    dispositivo,
                    indice,
                    out DevMode modo))
            {
                break;
            }

            if (modo.DmPelsWidth > 0
                && modo.DmPelsHeight > 0
                && modo.DmBitsPerPel >= 32)
            {
                modos.Add(
                    new ModoPantalla(
                        modo.DmPelsWidth,
                        modo.DmPelsHeight,
                        modo.DmDisplayFrequency));
            }
        }

        return modos
            .OrderBy(modo => modo.Ancho)
            .ThenBy(modo => modo.Alto)
            .ThenBy(modo => modo.Frecuencia)
            .ToArray();
    }

    private static PantallaWindows? ResolverPantalla(
        string selector,
        IReadOnlyList<PantallaWindows> pantallas)
    {
        string normalizado = selector
            .Trim()
            .ToLowerInvariant();

        if (normalizado is "primary" or "principal")
        {
            return pantallas.FirstOrDefault(
                pantalla => pantalla.Principal);
        }

        if (int.TryParse(
                normalizado,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int numero))
        {
            return pantallas.FirstOrDefault(
                pantalla => pantalla.Numero == numero);
        }

        return pantallas.FirstOrDefault(
            pantalla =>
                pantalla.Dispositivo.Equals(
                    selector,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static (int X, int Y) CalcularPosicionRelativa(
        PantallaWindows objetivo,
        PantallaWindows referencia,
        PosicionRelativaPantalla posicion)
    {
        return posicion switch
        {
            PosicionRelativaPantalla.Izquierda =>
                (referencia.X - objetivo.Ancho, referencia.Y),
            PosicionRelativaPantalla.Derecha =>
                (referencia.X + referencia.Ancho, referencia.Y),
            PosicionRelativaPantalla.Arriba =>
                (referencia.X, referencia.Y - objetivo.Alto),
            _ =>
                (referencia.X, referencia.Y + referencia.Alto)
        };
    }

    private static bool TryObtenerModo(
        string dispositivo,
        int indice,
        out DevMode modo)
    {
        modo = CrearDevMode();
        return EnumDisplaySettings(
            dispositivo,
            indice,
            ref modo);
    }

    private static DevMode CrearDevMode()
    {
        return new DevMode
        {
            DmDeviceName = string.Empty,
            DmFormName = string.Empty,
            DmSize = checked(
                (short)Marshal.SizeOf<DevMode>())
        };
    }

    private static DisplayDevice CrearDisplayDevice()
    {
        return new DisplayDevice
        {
            Cb = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceId = string.Empty,
            DeviceKey = string.Empty
        };
    }

    private static bool TryCantidad(
        IReadOnlyList<string> argumentos,
        int cantidad,
        string mensaje,
        out string error)
    {
        if (argumentos.Count == cantidad)
        {
            error = string.Empty;
            return true;
        }

        error = mensaje;
        return false;
    }

    private static bool TryEnteroPositivo(
        string texto,
        out int valor)
    {
        return int.TryParse(
                   texto,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out valor)
               && valor > 0;
    }

    private static bool TryEntero(
        string texto,
        out int valor)
    {
        return int.TryParse(
            texto,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out valor);
    }

    private static bool TryTopologia(
        string texto,
        out TopologiaPantallas topologia)
    {
        switch (texto.ToLowerInvariant())
        {
            case "extend":
            case "extended":
                topologia = TopologiaPantallas.Extendida;
                return true;
            case "clone":
            case "duplicate":
                topologia = TopologiaPantallas.Duplicada;
                return true;
            case "internal":
                topologia = TopologiaPantallas.SoloInterna;
                return true;
            case "external":
                topologia = TopologiaPantallas.SoloExterna;
                return true;
            default:
                topologia = default;
                return false;
        }
    }

    private static bool TryOrientacion(
        string texto,
        out OrientacionPantalla orientacion)
    {
        switch (texto.ToLowerInvariant())
        {
            case "landscape":
                orientacion = OrientacionPantalla.Horizontal;
                return true;
            case "portrait":
                orientacion = OrientacionPantalla.Vertical;
                return true;
            case "landscape-flipped":
                orientacion = OrientacionPantalla.HorizontalInvertida;
                return true;
            case "portrait-flipped":
                orientacion = OrientacionPantalla.VerticalInvertida;
                return true;
            default:
                orientacion = default;
                return false;
        }
    }

    private static bool TryPosicionRelativa(
        string texto,
        out PosicionRelativaPantalla posicion)
    {
        switch (texto.ToLowerInvariant())
        {
            case "left":
                posicion = PosicionRelativaPantalla.Izquierda;
                return true;
            case "right":
                posicion = PosicionRelativaPantalla.Derecha;
                return true;
            case "above":
                posicion = PosicionRelativaPantalla.Arriba;
                return true;
            case "below":
                posicion = PosicionRelativaPantalla.Abajo;
                return true;
            default:
                posicion = default;
                return false;
        }
    }

    private static bool Fallar(
        string mensaje,
        out string error)
    {
        error = mensaje;
        return false;
    }

    private static uint ObtenerBanderaTopologia(
        TopologiaPantallas topologia)
    {
        return topologia switch
        {
            TopologiaPantallas.Duplicada =>
                SdcTopologyClone,
            TopologiaPantallas.SoloInterna =>
                SdcTopologyInternal,
            TopologiaPantallas.SoloExterna =>
                SdcTopologyExternal,
            _ =>
                SdcTopologyExtend
        };
    }

    private static bool CodigoAceptado(int codigo)
    {
        return codigo is
            DispChangeSuccessful or DispChangeRestart;
    }

    private static ResultadoOperacion ResultadoCambioPantalla(
        int codigo,
        bool correcto,
        string detalle)
    {
        string descripcion = DescribirCodigoCambio(codigo);
        return new ResultadoOperacion(
            correcto,
            new
            {
                correcto,
                detalle = correcto
                    ? detalle
                    : detalle + " " + descripcion,
                codigo,
                resultado = descripcion,
                requiereReinicio =
                    codigo == DispChangeRestart
            });
    }

    private static ResultadoOperacion ResultadoCodigoWindows(
        int codigo,
        bool correcto,
        string detalle)
    {
        return new ResultadoOperacion(
            correcto,
            new
            {
                correcto,
                detalle = correcto
                    ? detalle
                    : $"{detalle} Código de Windows: {codigo}.",
                codigo
            });
    }

    private static ResultadoOperacion Correcto(
        string detalle,
        object? contenido = null)
    {
        return new ResultadoOperacion(
            true,
            contenido
            ?? new
            {
                correcto = true,
                detalle
            });
    }

    private static ResultadoOperacion Incorrecto(
        string detalle)
    {
        return new ResultadoOperacion(
            false,
            new
            {
                correcto = false,
                detalle
            });
    }

    private static string DescribirCodigoCambio(int codigo)
    {
        return codigo switch
        {
            0 => "Cambio aplicado.",
            1 => "Windows necesita reiniciarse para completar el cambio.",
            -1 => "El controlador de pantalla rechazó el cambio.",
            -2 => "El modo solicitado no es compatible.",
            -3 => "Windows recibió parámetros no válidos.",
            -4 => "No se pudo guardar la configuración.",
            -5 => "La combinación de banderas no es válida.",
            -6 => "La configuración DualView no admite el cambio.",
            _ => $"Código de cambio de pantalla {codigo}."
        };
    }

    private sealed record ResultadoOperacion(
        bool Correcto,
        object Contenido);

    private static readonly int[] EscalasDpi =
    [
        100,
        125,
        150,
        175,
        200,
        225,
        250,
        300,
        350,
        400,
        450,
        500
    ];

    private const int DisplayConfigDeviceInfoGetDpiScale = -3;
    private const int DisplayConfigDeviceInfoSetDpiScale = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeaderDpi
    {
        public int Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceDpiScaleGet
    {
        public DisplayConfigDeviceInfoHeaderDpi Header;
        public int MinScaleRel;
        public int CurScaleRel;
        public int MaxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceDpiScaleSet
    {
        public DisplayConfigDeviceInfoHeaderDpi Header;
        public int ScaleRel;
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string DmDeviceName;

        public short DmSpecVersion;
        public short DmDriverVersion;
        public short DmSize;
        public short DmDriverExtra;
        public int DmFields;
        public int DmPositionX;
        public int DmPositionY;
        public int DmDisplayOrientation;
        public int DmDisplayFixedOutput;
        public short DmColor;
        public short DmDuplex;
        public short DmYResolution;
        public short DmTtOption;
        public short DmCollate;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string DmFormName;

        public short DmLogPixels;
        public int DmBitsPerPel;
        public int DmPelsWidth;
        public int DmPelsHeight;
        public int DmDisplayFlags;
        public int DmDisplayFrequency;
        public int DmIcmMethod;
        public int DmIcmIntent;
        public int DmMediaType;
        public int DmDitherType;
        public int DmReserved1;
        public int DmReserved2;
        public int DmPanningWidth;
        public int DmPanningHeight;
    }

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDevice lpDisplayDevice,
        uint dwFlags);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        string lpszDeviceName,
        int iModeNum,
        ref DevMode lpDevMode);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        ref DevMode lpDevMode,
        nint hwnd,
        uint dwflags,
        nint lParam);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        nint lpDevMode,
        nint hwnd,
        uint dwflags,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        nint pathArray,
        uint numModeInfoArrayElements,
        nint modeInfoArray,
        uint flags);

    [DllImport(
        "user32.dll",
        EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfoDpi(
        ref DisplayConfigSourceDpiScaleGet requestPacket);

    [DllImport(
        "user32.dll",
        EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int DisplayConfigSetDeviceInfoDpi(
        ref DisplayConfigSourceDpiScaleSet setPacket);
}
