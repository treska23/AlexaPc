using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPCIA;

internal sealed record DependenciasTraductorLocal(
    Func<
        IReadOnlyList<MensajeOllama>,
        IReadOnlyList<HerramientaOllama>,
        CancellationToken,
        Task<MensajeOllama>> ConversarAsync,
    Func<
        string,
        CancellationToken,
        Task<ResultadoEjecucionPowerShell>> EjecutarAsync,
    Func<
        string,
        CancellationToken,
        Task<IReadOnlyList<string>>> ComprobarComandosAsync,
    Func<
        string,
        CancellationToken,
        Task<IReadOnlyList<TraduccionAprendida>>> BuscarAsync,
    Func<
        string,
        string,
        bool,
        CancellationToken,
        Task<bool>> AprenderAsync);

/// <summary>
/// Respaldo local de una sola llamada. El modelo únicamente traduce o
/// conversa; ControlPCIA valida y ejecuta el comando propuesto.
/// </summary>
internal static class TraductorLocalRapido
{
    private const int MaximoContexto = 12;
    private const int MaximoCaracteresMensaje = 1_500;

    internal const string InstruccionSistema =
        """
        Traduce lo que diga el usuario a comandos de consola PowerShell para Windows.
        Tú no ejecutas nada: ControlPCIA valida y ejecuta el texto que propongas.
        Responde una sola vez y usa exactamente una herramienta.

        Usa proponer_accion para cambiar algo del PC y proponer_consulta para obtener
        información. Usa responder_usuario si no hace falta consultar el PC. Usa
        preguntar_usuario sólo si falta una decisión imprescindible.

        Sólo hay tres restricciones:
        1. No eliminar elementos ni contenido.
        2. No mover ni cortar elementos.
        3. No formatear, limpiar ni reinicializar discos o unidades.

        Abrir o crear archivos, aplicaciones, páginas y configuraciones sí está
        permitido. No inventes rutas, ejecutables, cmdlets ni resultados: descúbrelos
        dentro del propio comando o pregunta. No propongas ratón, teclado, SendKeys,
        UI Automation ni reconocimiento gráfico.

        Para Escritorio, Documentos, Música y otras carpetas conocidas usa
        [Environment]::GetFolderPath(...); nunca supongas que cuelgan de SystemRoot o
        USERPROFILE. No fabriques archivos de proyecto, extensiones, argumentos de
        inicio ni interfaces de consola de una aplicación. Si no conoces una interfaz
        real, usa proponer_consulta para investigarla o preguntar_usuario. Crear una
        carpeta normal sí puede hacerse con New-Item una vez resuelta su ruta real.

        Comandos propios disponibles:
        - ControlPCIA.exe display list|modes|primary|resolution|frequency|scale|enable|disable|topology|orientation|position|place
        - ControlPCIA.exe window --match 'texto' [--list|--foreground|--state normal|maximized|minimized|--close|--x N --y N --width N --height N]
        - ControlPCIA.exe media status|play|pause|toggle|stop|next|previous|forward|rewind|seek|shuffle|repeat|rate|fullscreen|exit-fullscreen [--app nombre]

        Devuelve comandos completos, no explicaciones alrededor del comando.
        """;

    internal static IReadOnlyList<HerramientaOllama> Herramientas { get; } =
    [
        CrearHerramienta(
            "proponer_accion",
            "Entrega a ControlPCIA un script PowerShell completo para realizar toda la acción solicitada. El programa lo validará y ejecutará una sola vez, sin comprobaciones gráficas posteriores.",
            "comando",
            "Script PowerShell completo. Puede contener varias instrucciones secuenciales."),
        CrearHerramienta(
            "proponer_consulta",
            "Entrega una consulta PowerShell de sólo lectura para responder con información real del PC.",
            "comando",
            "Consulta PowerShell limitada y con salida textual útil para el usuario."),
        CrearHerramienta(
            "preguntar_usuario",
            "Pide únicamente el dato o la decisión imprescindible que falta.",
            "pregunta",
            "Pregunta breve y concreta en español."),
        CrearHerramienta(
            "responder_usuario",
            "Responde directamente cuando no hace falta consultar ni cambiar el PC.",
            "respuesta",
            "Respuesta breve, directa y en español.")
    ];

    public static Task<ResultadoControl> ControlarAsync(
        string instruccion,
        IReadOnlyList<MensajeConversacionControl>? contexto = null,
        CancellationToken cancellationToken = default,
        bool soloTraducir = false)
    {
        var dependencias = new DependenciasTraductorLocal(
            ClienteOllama.ConversarAsync,
            static (comando, cancelacion) =>
                EjecutorPowerShell.EjecutarAsync(
                    comando,
                    cancelacion),
            ComprobadorComandosPowerShell.ObtenerNoDisponiblesAsync,
            static (peticion, cancelacion) =>
                MemoriaTraducciones.Predeterminada.BuscarAsync(
                    peticion,
                    cancellationToken: cancelacion),
            static (peticion, comando, consulta, cancelacion) =>
                MemoriaTraducciones.Predeterminada.AprenderAsync(
                    peticion,
                    comando,
                    consulta,
                    cancelacion));

        return ControlarConDependenciasAsync(
            instruccion,
            contexto,
            soloTraducir,
            dependencias,
            cancellationToken);
    }

    internal static async Task<ResultadoControl>
        ControlarConDependenciasAsync(
            string instruccion,
            IReadOnlyList<MensajeConversacionControl>? contexto,
            bool soloTraducir,
            DependenciasTraductorLocal dependencias,
            CancellationToken cancellationToken)
    {
        string peticion =
            (instruccion ?? string.Empty).Trim();

        if (peticion.Length == 0)
        {
            return Error(
                "orden_vacia",
                "No he recibido ninguna orden.");
        }

        if (peticion.Length > 2_000)
        {
            return Error(
                "orden_demasiado_larga",
                "La orden supera los 2000 caracteres.");
        }

        IReadOnlyList<TraduccionAprendida> aprendidas;

        try
        {
            aprendidas =
                await dependencias.BuscarAsync(
                    peticion,
                    cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            aprendidas = [];
        }

        TraduccionAprendida? exacta =
            contexto is null or { Count: 0 }
                ? aprendidas.FirstOrDefault(
                    traduccion =>
                        traduccion.Similitud
                        >= 0.999_999)
                : null;

        if (exacta is not null)
        {
            return await EjecutarComandoAsync(
                peticion,
                exacta.Comando,
                exacta.Consulta,
                soloTraducir,
                dependencias,
                cancellationToken,
                "memoria");
        }

        var mensajes = new List<MensajeOllama>
        {
            new("system", InstruccionSistema)
        };
        mensajes.AddRange(
            NormalizarContexto(contexto)
                .Select(mensaje =>
                    new MensajeOllama(
                        mensaje.Rol,
                        mensaje.Texto)));
        mensajes.Add(
            new MensajeOllama(
                "user",
                CrearPeticion(
                    peticion,
                    aprendidas)));

        MensajeOllama respuesta;

        try
        {
            respuesta =
                await dependencias.ConversarAsync(
                    mensajes,
                    Herramientas,
                    cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(
                "ia_local_no_disponible",
                "No he podido traducir esta orden con la IA local: "
                + ex.Message);
        }

        IReadOnlyList<LlamadaHerramientaOllama> llamadas =
            respuesta.LlamadasHerramientas
            ?? [];

        if (llamadas.Count == 0)
        {
            string contenido =
                (respuesta.Contenido ?? string.Empty).Trim();
            return contenido.Length > 0
                ? new ResultadoControl(
                    true,
                    "conversacion",
                    contenido,
                    [],
                    false)
                : Error(
                    "respuesta_ia_invalida",
                    "La IA local no ha devuelto una traducción.");
        }

        if (llamadas.Count != 1)
        {
            return Error(
                "respuesta_ia_invalida",
                "La IA local ha devuelto más de una propuesta. No he ejecutado ninguna.");
        }

        LlamadaHerramientaOllama llamada =
            llamadas[0];
        string nombre =
            llamada.Funcion.Nombre.Trim();

        if (nombre.Equals(
                "preguntar_usuario",
                StringComparison.OrdinalIgnoreCase))
        {
            string pregunta =
                ObtenerArgumento(
                    llamada,
                    "pregunta");
            return pregunta.Length > 0
                ? Error(
                    "requiere_aclaracion",
                    pregunta)
                : Error(
                    "respuesta_ia_invalida",
                    "La IA local no ha indicado qué dato necesita.");
        }

        if (nombre.Equals(
                "responder_usuario",
                StringComparison.OrdinalIgnoreCase))
        {
            string texto =
                ObtenerArgumento(
                    llamada,
                    "respuesta");
            return texto.Length > 0
                ? new ResultadoControl(
                    true,
                    "conversacion",
                    texto,
                    [],
                    false)
                : Error(
                    "respuesta_ia_invalida",
                    "La IA local no ha incluido la respuesta.");
        }

        bool consulta =
            nombre.Equals(
                "proponer_consulta",
                StringComparison.OrdinalIgnoreCase);

        if (!consulta
            && !nombre.Equals(
                "proponer_accion",
                StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                "respuesta_ia_invalida",
                $"La IA local ha usado una herramienta desconocida: {nombre}.");
        }

        string comando =
            ObtenerArgumento(
                llamada,
                "comando");
        return await EjecutarComandoAsync(
            peticion,
            comando,
            consulta,
            soloTraducir,
            dependencias,
            cancellationToken,
            "ia");
    }

    internal static async Task PrecalentarAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await ClienteOllama.ConversarAsync(
                [
                    new(
                        "system",
                        "Usa responder_usuario."),
                    new(
                        "user",
                        "Responde LISTO.")
                ],
                Herramientas,
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            // El traductor devolverá el error real si una petición necesita IA.
        }
    }

    private static async Task<ResultadoControl>
        EjecutarComandoAsync(
            string peticion,
            string comando,
            bool consulta,
            bool soloTraducir,
            DependenciasTraductorLocal dependencias,
            CancellationToken cancellationToken,
            string origen)
    {
        comando =
            (comando ?? string.Empty).Trim();
        ResultadoValidacionPowerShell validacion =
            ValidadorPowerShell.Validar(comando);
        bool compatibleConsola =
            ControlWindows
                .EsComandoCompatibleConModoConsola(
                    comando);
        bool coherente =
            ValidarCoherencia(
                peticion,
                comando,
                out string errorCoherencia);

        if (!validacion.Permitido
            || !compatibleConsola
            || !coherente)
        {
            string detalle =
                !validacion.Permitido
                    ? validacion.Motivo
                    : !compatibleConsola
                        ? "La propuesta intenta usar automatización gráfica."
                        : errorCoherencia;
            return Error(
                "comando_bloqueado",
                "No he ejecutado la propuesta: "
                + detalle);
        }

        bool realmenteConsulta =
            ControlWindows.EsComandoDeConsulta(
                comando);

        if (consulta && !realmenteConsulta)
        {
            return Error(
                "consulta_no_segura",
                "La IA local presentó como consulta un comando que cambia el PC. No lo he ejecutado.");
        }

        try
        {
            IReadOnlyList<string> noDisponibles =
                await dependencias
                    .ComprobarComandosAsync(
                        comando,
                        cancellationToken);

            if (noDisponibles.Count > 0)
            {
                return Error(
                    "comando_no_disponible",
                    "PowerShell confirma que no existen estos comandos en el PC: "
                    + string.Join(
                        ", ",
                        noDisponibles));
            }
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            return Error(
                "comprobacion_fallida",
                "No he podido comprobar los comandos propuestos: "
                + ex.Message);
        }

        var pasoPreparado =
            new ResultadoPasoControl(
                1,
                comando,
                false,
                0,
                string.Empty,
                string.Empty);

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                origen == "memoria"
                    ? "He recuperado una traducción aprendida; el modo de prueba no la ejecuta."
                    : "La IA local ha traducido la orden; el modo de prueba no la ejecuta.",
                [pasoPreparado],
                origen == "memoria");
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

        if (!ejecucion.Ejecutado
            || ejecucion.CodigoSalida != 0
            || !string.IsNullOrWhiteSpace(
                ejecucion.Error))
        {
            string error =
                string.IsNullOrWhiteSpace(
                    ejecucion.Error)
                    ? $"código de salida {ejecucion.CodigoSalida}"
                    : ejecucion.Error;
            return new ResultadoControl(
                false,
                "error_comando",
                "Windows no ha completado el comando: "
                + error,
                [paso],
                false);
        }

        bool aprendido = false;

        try
        {
            aprendido =
                await dependencias.AprenderAsync(
                    peticion,
                    comando,
                    consulta || realmenteConsulta,
                    cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
        }

        string salida =
            ejecucion.Salida.Trim();
        string mensaje =
            salida.Length > 0
                ? salida
                : realmenteConsulta
                    ? "La consulta se ha completado sin devolver datos."
                    : "Windows ha aceptado el comando.";

        return new ResultadoControl(
            true,
            realmenteConsulta
                ? "respuesta"
                : "completado",
            mensaje,
            [paso],
            aprendido);
    }

    private static string ObtenerArgumento(
        LlamadaHerramientaOllama llamada,
        string nombre)
    {
        return llamada.Funcion.Argumentos.TryGetValue(
                   nombre,
                   out JsonElement valor)
               && valor.ValueKind
               == JsonValueKind.String
            ? (valor.GetString() ?? string.Empty).Trim()
            : string.Empty;
    }

    private static bool ValidarCoherencia(
        string peticion,
        string comando,
        out string error)
    {
        if (Regex.IsMatch(
                comando,
                @"\$(?:env:)?(?:SystemRoot|windir)\b[^\r\n;|]*(?:Desktop|Escritorio|Documents|Documentos|Music|Música|Pictures|Imágenes|Videos)",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant))
        {
            error =
                "La propuesta ha supuesto una ruta de carpeta personal dentro de Windows. Debe resolverla con Environment.GetFolderPath.";
            return false;
        }

        string intencion =
            MemoriaRecetas.Normalizar(peticion);

        if (Regex.IsMatch(
                intencion,
                @"\b(?:crea|crear|nuevo|nueva)\b.*\bproyecto\b",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(
                comando,
                @"\bNew-Item\b[^\r\n;|]*\bItemType\s+(?:File|'File'|""File"")",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant))
        {
            error =
                "La propuesta intenta fabricar un archivo de proyecto vacío. Debe usar una interfaz de consola real de la aplicación o pedir más información.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static IReadOnlyList<MensajeConversacionControl>
        NormalizarContexto(
            IReadOnlyList<MensajeConversacionControl>? contexto)
    {
        if (contexto is null || contexto.Count == 0)
        {
            return [];
        }

        return contexto
            .TakeLast(MaximoContexto)
            .Where(mensaje =>
                mensaje.Rol.Equals(
                    "user",
                    StringComparison.OrdinalIgnoreCase)
                || mensaje.Rol.Equals(
                    "assistant",
                    StringComparison.OrdinalIgnoreCase))
            .Select(mensaje =>
                new MensajeConversacionControl(
                    mensaje.Rol.ToLowerInvariant(),
                    (mensaje.Texto ?? string.Empty)
                        .Trim()[..Math.Min(
                            (mensaje.Texto ?? string.Empty)
                                .Trim().Length,
                            MaximoCaracteresMensaje)]))
            .Where(mensaje =>
                mensaje.Texto.Length > 0)
            .ToArray();
    }

    private static string CrearPeticion(
        string peticion,
        IReadOnlyList<TraduccionAprendida> aprendidas)
    {
        var texto =
            new StringBuilder();
        texto.AppendLine(
            "Petición actual:");
        texto.AppendLine(peticion);

        TraduccionAprendida[] referencias =
            aprendidas
                .Where(aprendida =>
                    aprendida.Similitud < 0.999_999)
                .Take(3)
                .ToArray();

        if (referencias.Length > 0)
        {
            texto.AppendLine();
            texto.AppendLine(
                "Traducciones que funcionaron antes; son referencias, no resultados actuales:");

            foreach (TraduccionAprendida referencia in referencias)
            {
                texto.Append("- ");
                texto.Append(referencia.Intencion);
                texto.Append(" => ");
                texto.AppendLine(referencia.Comando);
            }
        }

        return texto.ToString().TrimEnd();
    }

    private static HerramientaOllama CrearHerramienta(
        string nombre,
        string descripcion,
        string propiedad,
        string descripcionPropiedad)
    {
        return new HerramientaOllama(
            "function",
            new FuncionHerramientaOllama(
                nombre,
                descripcion,
                new ParametrosHerramientaOllama(
                    "object",
                    new Dictionary<
                        string,
                        PropiedadHerramientaOllama>
                    {
                        [propiedad] = new(
                            "string",
                            descripcionPropiedad)
                    },
                    [propiedad])));
    }

    private static ResultadoControl Error(
        string estado,
        string mensaje)
    {
        return new ResultadoControl(
            false,
            estado,
            mensaje,
            [],
            false);
    }
}
