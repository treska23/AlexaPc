using System.Management.Automation.Language;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPCIA;

internal sealed record DependenciasControlWindows(
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
        Task<IReadOnlyList<RecetaReferencia>>> BuscarAprendidoAsync,
    Func<
        string,
        CancellationToken,
        Task<string>> ObtenerContextoLocalAsync,
    Func<
        string,
        CancellationToken,
        Task<IReadOnlyList<string>>> ComprobarComandosAsync,
    Func<
        string,
        IReadOnlyList<string>,
        CancellationToken,
        Task<bool>> AprenderAsync);

internal static class ControlWindows
{
    private const int MaximoPasos = 8;
    private const int MaximoCaracteresContexto = 8_000;
    private const int MaximoMensajesContexto = 12;

    internal const string InstruccionSistema =
        """
        Traduce lo que diga el usuario a comandos de consola PowerShell para Windows.

        Usa proponer_consulta si necesitas información real del equipo, de PowerShell
        o de Internet. No inventes nombres, rutas, procesos ni resultados. Usa
        proponer_comando sólo cuando tengas un comando completo para toda la petición
        y una consulta que devuelva evidencia explícita de que funcionó. El programa,
        no tú, validará y ejecutará los textos propuestos. Usa preguntar_usuario sólo
        cuando falte una decisión que no puedas deducir.

        Las consultas deben devolver únicamente los datos necesarios y como máximo
        50 resultados. Cuando ControlPCIA te entregue un resultado, responde de forma
        breve, directa y en español, sin añadir datos que no aparezcan en él.

        Sólo hay tres restricciones:
        1. No eliminar elementos ni contenido.
        2. No mover ni cortar elementos.
        3. No formatear, limpiar ni reinicializar discos o unidades.
        """;

    internal static IReadOnlyList<HerramientaOllama> Herramientas { get; } =
    [
        CrearHerramienta(
            "proponer_consulta",
            "Propone una consulta PowerShell de sólo lectura, concreta y limitada, para que ControlPCIA obtenga sólo la información necesaria del PC, descubra comandos o consulte Internet. Para localizar aplicaciones instaladas usa datos reales como Get-StartApps, App Paths, accesos directos o Get-Command; consultar procesos sólo indica lo que ya está abierto.",
            new Dictionary<string, PropiedadHerramientaOllama>
            {
                ["comando"] = new(
                    "string",
                    "Comando PowerShell de sólo lectura. No puede cambiar el estado del PC.")
            },
            ["comando"]),
        CrearHerramienta(
            "proponer_comando",
            "Propone a ControlPCIA una acción PowerShell y una consulta de sólo lectura que demuestre el resultado.",
            new Dictionary<string, PropiedadHerramientaOllama>
            {
                ["comando"] = new(
                    "string",
                    "Script PowerShell completo que ControlPCIA debe ejecutar para realizar toda la petición del usuario."),
                ["verificacion"] = new(
                    "string",
                    "Consulta PowerShell de sólo lectura que devuelve texto explícito demostrando el resultado después de la acción. Si se abre una aplicación debe comprobar su proceso o ventana en ejecución; comprobar sólo el archivo, Get-Command o la instalación no demuestra que se haya abierto.")
            },
            ["comando", "verificacion"]),
        CrearHerramienta(
            "preguntar_usuario",
            "Solicita al usuario un dato o una decisión imprescindible antes de continuar.",
            new Dictionary<string, PropiedadHerramientaOllama>
            {
                ["pregunta"] = new(
                    "string",
                    "Pregunta breve, concreta y en español.")
            },
            ["pregunta"])
    ];

    public static Task<ResultadoControl> ControlarAsync(
        string instruccion,
        Action<EventoControl>? informar = null,
        IReadOnlyList<MensajeConversacionControl>? contextoConversacion = null,
        CancellationToken cancellationToken = default,
        bool soloTraducir = false)
    {
        var dependencias = new DependenciasControlWindows(
            ClienteOllama.ConversarAsync,
            static (comando, cancelacion) =>
                EjecutorPowerShell.EjecutarAsync(
                    comando,
                    cancelacion),
            static (peticion, cancelacion) =>
                MemoriaRecetas.Predeterminada.BuscarAsync(
                    peticion,
                    cancellationToken: cancelacion),
            InventarioAplicaciones.ObtenerContextoRelacionadoAsync,
            ComprobadorComandosPowerShell.ObtenerNoDisponiblesAsync,
            static (peticion, comandos, cancelacion) =>
                MemoriaRecetas.Predeterminada.AprenderAsync(
                    peticion,
                    comandos,
                    cancelacion));

        return ControlarConDependenciasAsync(
            instruccion,
            informar,
            contextoConversacion,
            cancellationToken,
            soloTraducir,
            dependencias);
    }

    internal static async Task<ResultadoControl>
        ControlarConDependenciasAsync(
            string instruccion,
            Action<EventoControl>? informar,
            IReadOnlyList<MensajeConversacionControl>? contextoConversacion,
            CancellationToken cancellationToken,
            bool soloTraducir,
            DependenciasControlWindows dependencias)
    {
        if (string.IsNullOrWhiteSpace(instruccion))
        {
            return Finalizar(
                false,
                "orden_vacia",
                "No se ha recibido ninguna orden.",
                [],
                false,
                informar);
        }

        string peticion = instruccion.Trim();

        if (peticion.Length > 1_000)
        {
            return Finalizar(
                false,
                "orden_demasiado_larga",
                "La orden supera los 1000 caracteres.",
                [],
                false,
                informar);
        }

        Informar(
            informar,
            new EventoControl(
                "pensando",
                "ControlPCIA está interpretando la petición."));

        IReadOnlyList<RecetaReferencia> aprendidos =
            await BuscarAprendidosAsync(
                peticion,
                dependencias,
                informar,
                cancellationToken);
        var pasos = new List<ResultadoPasoControl>();
        IReadOnlyList<MensajeConversacionControl> contextoNormalizado =
            NormalizarContexto(contextoConversacion);
        RecetaReferencia? exacta =
            contextoNormalizado.Count == 0
                ? aprendidos.FirstOrDefault(receta =>
                    receta.Similitud >= 0.999_999
                    && receta.Comandos.Any(comando =>
                        !EsComandoDeConsulta(comando)))
                : null;

        if (exacta is not null)
        {
            ResultadoControl? resultadoAprendido =
                await IntentarRutaAprendidaExactaAsync(
                    peticion,
                    exacta,
                    pasos,
                    soloTraducir,
                    dependencias,
                    informar,
                    cancellationToken);

            if (resultadoAprendido is not null)
            {
                return resultadoAprendido;
            }

            aprendidos = aprendidos
                .Where(receta => !ReferenceEquals(receta, exacta))
                .ToArray();
        }

        var mensajes = new List<MensajeOllama>
        {
            new("system", InstruccionSistema)
        };
        string contextoLocal =
            await ObtenerContextoLocalAsync(
                peticion,
                dependencias,
                cancellationToken);

        mensajes.AddRange(
            contextoNormalizado
                .Select(mensaje =>
                    new MensajeOllama(
                        mensaje.Rol,
                        mensaje.Texto)));
        mensajes.Add(
            new MensajeOllama(
                "user",
                CrearMensajePeticion(
                    peticion,
                    aprendidos,
                    contextoLocal)));

        if (pasos.Count > 0)
        {
            mensajes.Add(
                new MensajeOllama(
                    "user",
                    CrearMensajeIntentoAprendidoFallido(
                        pasos)));
        }

        var propuestas = new HashSet<string>(
            StringComparer.Ordinal);
        bool pideAccion = EsPeticionDeAccion(peticion);
        bool consultaPendiente = false;
        bool consultaUtil = false;

        for (int intento = 1; intento <= MaximoPasos; intento++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Informar(
                informar,
                new EventoControl(
                    "pensando",
                    intento == 1
                        ? "La IA local está preparando la propuesta."
                        : "La IA local está usando el resultado real de PowerShell."));

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
                return Finalizar(
                    false,
                    "error_ia",
                    "No se pudo obtener la propuesta de la IA local: " + ex.Message,
                    pasos,
                    false,
                    informar);
            }

            string contenido =
                (respuesta.Contenido ?? string.Empty).Trim();
            LlamadaHerramientaOllama? llamada =
                respuesta.LlamadasHerramientas?.FirstOrDefault();

            if (llamada is null)
            {
                if (TryExtraerPregunta(
                        contenido,
                        out string pregunta))
                {
                    return Finalizar(
                        false,
                        "requiere_aclaracion",
                        pregunta,
                        pasos,
                        false,
                        informar);
                }

                if (pideAccion || consultaPendiente)
                {
                    mensajes.Add(
                        new MensajeOllama(
                            "assistant",
                            contenido));
                    mensajes.Add(
                        new MensajeOllama(
                            "user",
                            consultaPendiente
                                ? "La consulta anterior falló o fue demasiado amplia. Propón otra consulta más concreta; no respondas todavía."
                                : "Todavía no se ha ejecutado la acción solicitada. Usa una herramienta; no afirmes que la tarea terminó."));
                    continue;
                }

                if (contenido.Length == 0)
                {
                    if (consultaUtil)
                    {
                        contenido = pasos.Last(paso =>
                            EsPasoCorrecto(paso)
                            && EsComandoDeConsulta(paso.Comando)).Salida;
                    }

                    if (contenido.Length == 0)
                    {
                        continue;
                    }
                }

                return Finalizar(
                    true,
                    consultaUtil ? "respuesta" : "conversacion",
                    contenido,
                    pasos,
                    false,
                    informar);
            }

            llamada = llamada with
            {
                Funcion = llamada.Funcion with
                {
                    Nombre = llamada.Funcion.Nombre.Trim()
                }
            };
            mensajes.Add(
                respuesta with
                {
                    LlamadasHerramientas = [llamada]
                });

            string nombreHerramienta =
                llamada.Funcion.Nombre;

            if (nombreHerramienta.Equals(
                    "preguntar_usuario",
                    StringComparison.OrdinalIgnoreCase))
            {
                string pregunta = ObtenerArgumento(
                    llamada,
                    "pregunta");

                if (pregunta.Length == 0)
                {
                    AgregarResultadoHerramienta(
                        mensajes,
                        nombreHerramienta,
                        false,
                        "Falta el parámetro pregunta.");
                    continue;
                }

                return Finalizar(
                    false,
                    "requiere_aclaracion",
                    pregunta,
                    pasos,
                    false,
                    informar);
            }

            if (nombreHerramienta.Equals(
                    "proponer_consulta",
                    StringComparison.OrdinalIgnoreCase))
            {
                string comando = ObtenerArgumento(
                    llamada,
                    "comando");

                if (!TryValidarComando(
                        comando,
                        debeSerConsulta: true,
                        pasos,
                        informar,
                        out string errorValidacion))
                {
                    ResultadoControl? bloqueo =
                        FinalizarSiHayBloqueo(
                            pasos,
                            informar);

                    if (bloqueo is not null)
                    {
                        return bloqueo;
                    }

                    AgregarResultadoHerramienta(
                        mensajes,
                        nombreHerramienta,
                        false,
                        errorValidacion);
                    continue;
                }

                if (!propuestas.Add(
                        "proponer_consulta\n" + comando))
                {
                    return Finalizar(
                        false,
                        "sin_progreso",
                        "La IA local repitió la misma consulta sin avanzar.",
                        pasos,
                        false,
                        informar);
                }

                // El modo de traducción puede ejecutar consultas validadas
                // de solo lectura para descubrir rutas, procesos o nombres
                // reales. La acción final continúa sin ejecutarse.
                ResultadoEjecucionPowerShell ejecucion =
                    await EjecutarAsync(
                        comando,
                        dependencias,
                        informar,
                        cancellationToken);
                ResultadoPasoControl paso = RegistrarPaso(
                    comando,
                    ejecucion,
                    pasos,
                    informar);
                bool correcto = EsPasoCorrecto(paso);
                bool demasiadoAmplio =
                    correcto
                    && EsResultadoDemasiadoAmplio(
                        ejecucion.Salida);
                consultaPendiente =
                    !correcto || demasiadoAmplio;
                consultaUtil |=
                    correcto && !demasiadoAmplio;
                AgregarResultadoHerramienta(
                    mensajes,
                    nombreHerramienta,
                    correcto && !demasiadoAmplio,
                    demasiadoAmplio
                        ? CrearResultadoDemasiadoAmplio(
                            ejecucion.Salida)
                        : CrearResultadoHerramienta(
                            "consulta",
                            ejecucion));
                continue;
            }

            if (nombreHerramienta.Equals(
                    "proponer_comando",
                    StringComparison.OrdinalIgnoreCase))
            {
                string comando = ObtenerArgumento(
                    llamada,
                    "comando");
                string verificacion = ObtenerArgumento(
                    llamada,
                    "verificacion");
                verificacion =
                    NormalizarVerificacionVentanas(
                        verificacion);

                bool comandoValido = TryValidarComando(
                    comando,
                    debeSerConsulta: false,
                    pasos,
                    informar,
                    out string errorComando);
                bool verificacionValida = TryValidarComando(
                    verificacion,
                    debeSerConsulta: true,
                    pasos,
                    informar,
                    out string errorVerificacion);

                if (comandoValido)
                {
                    IReadOnlyList<string> noDisponibles =
                        await dependencias.ComprobarComandosAsync(
                            comando,
                            cancellationToken);

                    if (noDisponibles.Count > 0)
                    {
                        comandoValido = false;
                        errorComando =
                            "PowerShell confirma que estos comandos no existen en este PC: "
                            + string.Join(", ", noDisponibles)
                            + ". Propón una alternativa real; no ejecutes parcialmente la petición.";
                    }
                }

                if (verificacionValida)
                {
                    IReadOnlyList<string> noDisponibles =
                        await dependencias.ComprobarComandosAsync(
                            verificacion,
                            cancellationToken);

                    if (noDisponibles.Count > 0)
                    {
                        verificacionValida = false;
                        errorVerificacion =
                            "PowerShell confirma que la verificación usa comandos inexistentes: "
                            + string.Join(", ", noDisponibles)
                            + ".";
                    }
                }

                if (!comandoValido || !verificacionValida)
                {
                    ResultadoControl? bloqueo =
                        FinalizarSiHayBloqueo(
                            pasos,
                            informar);

                    if (bloqueo is not null)
                    {
                        return bloqueo;
                    }

                    AgregarResultadoHerramienta(
                        mensajes,
                        nombreHerramienta,
                        false,
                        errorComando.Length > 0
                            ? errorComando
                            : errorVerificacion);
                    continue;
                }

                string clave =
                    "proponer_comando\n"
                    + comando
                    + "\n"
                    + verificacion;

                if (!propuestas.Add(clave))
                {
                    return Finalizar(
                        false,
                        "sin_progreso",
                        "La IA local repitió la misma acción sin resolver la petición.",
                        pasos,
                        false,
                        informar);
                }

                if (soloTraducir)
                {
                    return FinalizarModoPrueba(
                        [comando, verificacion],
                        pasos,
                        informar);
                }

                ResultadoEjecucionPowerShell ejecucion =
                    await EjecutarAsync(
                        comando,
                        dependencias,
                        informar,
                        cancellationToken);
                ResultadoPasoControl pasoAccion = RegistrarPaso(
                    comando,
                    ejecucion,
                    pasos,
                    informar);

                if (!EsPasoCorrecto(pasoAccion))
                {
                    AgregarResultadoHerramienta(
                        mensajes,
                        nombreHerramienta,
                        false,
                        CrearResultadoHerramienta(
                            "acción",
                            ejecucion));
                    continue;
                }

                ResultadoEjecucionPowerShell comprobacion =
                    await EjecutarAsync(
                        verificacion,
                        dependencias,
                        informar,
                        cancellationToken);
                ResultadoPasoControl pasoVerificacion =
                    RegistrarPaso(
                        verificacion,
                        comprobacion,
                        pasos,
                        informar);
                bool verificado =
                    EsPasoCorrecto(pasoVerificacion)
                    && VerificacionDemuestraResultado(
                        comando,
                        verificacion,
                        comprobacion.Salida);

                if (!verificado)
                {
                    AgregarResultadoHerramienta(
                        mensajes,
                        nombreHerramienta,
                        false,
                        CrearResultadoHerramienta(
                            "verificación sin evidencia",
                            comprobacion));
                    continue;
                }

                bool aprendido =
                    await AprenderAsync(
                        peticion,
                        pasos,
                        dependencias,
                        informar,
                        cancellationToken);

                return Finalizar(
                    true,
                    "completado",
                    CrearMensajeExitoVerificado(
                        comprobacion.Salida),
                    pasos,
                    aprendido,
                    informar);
            }

            AgregarResultadoHerramienta(
                mensajes,
                nombreHerramienta,
                false,
                "Herramienta desconocida. Usa proponer_consulta, proponer_comando o preguntar_usuario.");
        }

        ResultadoPasoControl? ultimoPaso =
            pasos.LastOrDefault();

        return Finalizar(
            false,
            "limite_pasos",
            ultimoPaso is null
                ? "La IA local no eligió una propuesta válida dentro del límite de pasos."
                : "No se pudo completar y verificar la petición. "
                  + (string.IsNullOrWhiteSpace(ultimoPaso.Error)
                      ? "La última comprobación no aportó evidencia."
                      : ultimoPaso.Error),
            pasos,
            false,
            informar);
    }

    private static async Task<ResultadoControl?>
        IntentarRutaAprendidaExactaAsync(
            string peticion,
            RecetaReferencia receta,
            ICollection<ResultadoPasoControl> pasos,
            bool soloTraducir,
            DependenciasControlWindows dependencias,
            Action<EventoControl>? informar,
            CancellationToken cancellationToken)
    {
        if (receta.Comandos.Count == 0
            || receta.Comandos.Any(comando =>
                !ValidadorPowerShell.Validar(comando).Permitido))
        {
            return null;
        }

        Informar(
            informar,
            new EventoControl(
                "memoria",
                "ControlPCIA reconoce exactamente esta petición y probará la secuencia que ya funcionó, sin consultar a la IA."));

        if (soloTraducir)
        {
            return FinalizarModoPrueba(
                receta.Comandos,
                pasos.ToArray(),
                informar);
        }

        foreach (string comando in receta.Comandos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResultadoEjecucionPowerShell ejecucion =
                await EjecutarAsync(
                    comando,
                    dependencias,
                    informar,
                    cancellationToken);
            ResultadoPasoControl paso = RegistrarPaso(
                comando,
                ejecucion,
                pasos,
                informar);

            if (!EsPasoCorrecto(paso))
            {
                Informar(
                    informar,
                    new EventoControl(
                        "memoria_error",
                        "La secuencia aprendida dejó de funcionar; ControlPCIA pedirá una propuesta nueva a la IA."));
                return null;
            }
        }

        bool contieneAccion = receta.Comandos.Any(comando =>
            !EsComandoDeConsulta(comando));
        ResultadoPasoControl ultimo = pasos.Last();

        if (contieneAccion
            &&
            (RequiereVerificacionTrasCambio(
                 pasos.ToArray())
             || !EsComandoDeConsulta(ultimo.Comando)
             || string.IsNullOrWhiteSpace(ultimo.Salida)))
        {
            Informar(
                informar,
                new EventoControl(
                    "memoria_error",
                    "La secuencia aprendida no aportó una comprobación suficiente; ControlPCIA solicitará una propuesta nueva."));
            return null;
        }

        bool aprendido =
            await AprenderAsync(
                peticion,
                pasos.ToArray(),
                dependencias,
                informar,
                cancellationToken);
        string mensaje = contieneAccion
            ? CrearMensajeExitoVerificado(ultimo.Salida)
            : string.IsNullOrWhiteSpace(ultimo.Salida)
                ? "La consulta no devolvió resultados."
                : ultimo.Salida;

        return Finalizar(
            true,
            contieneAccion ? "completado" : "respuesta",
            mensaje,
            pasos.ToArray(),
            aprendido,
            informar);
    }

    private static ResultadoControl? FinalizarSiHayBloqueo(
        IReadOnlyList<ResultadoPasoControl> pasos,
        Action<EventoControl>? informar)
    {
        ResultadoPasoControl? bloqueo = pasos.LastOrDefault(paso =>
            !paso.Ejecutado
            && paso.Error.StartsWith(
                "BLOQUEADO:",
                StringComparison.Ordinal));

        return bloqueo is null
            ? null
            : Finalizar(
                false,
                "comando_rechazado",
                bloqueo.Error,
                pasos,
                false,
                informar);
    }

    private static string CrearMensajeIntentoAprendidoFallido(
        IReadOnlyList<ResultadoPasoControl> pasos)
    {
        var texto = new StringBuilder();
        texto.AppendLine(
            "CONTROLPCIA YA PROBÓ UNA SECUENCIA APRENDIDA Y NO PUDO VERIFICARLA.");
        texto.AppendLine(
            "No repitas esa misma estrategia sin corregirla. Resultados reales:");

        foreach (ResultadoPasoControl paso in pasos.TakeLast(6))
        {
            texto.AppendLine();
            texto.AppendLine("COMANDO: " + paso.Comando);
            texto.AppendLine(
                $"CODIGO_SALIDA={paso.CodigoSalida}");
            texto.AppendLine(
                "STDOUT: "
                + LimitarParaModelo(
                    paso.Salida));
            texto.AppendLine(
                "STDERR: "
                + LimitarParaModelo(
                    paso.Error));
        }

        return texto.ToString().TrimEnd();
    }

    private static string LimitarParaModelo(string texto)
    {
        const int maximo = 1_500;
        string valor = string.IsNullOrWhiteSpace(texto)
            ? "(vacío)"
            : texto.Trim();

        return valor.Length <= maximo
            ? valor
            : valor[..maximo] + " [abreviado]";
    }

    private static HerramientaOllama CrearHerramienta(
        string nombre,
        string descripcion,
        IReadOnlyDictionary<
            string,
            PropiedadHerramientaOllama> propiedades,
        IReadOnlyList<string> requeridos)
    {
        return new HerramientaOllama(
            "function",
            new FuncionHerramientaOllama(
                nombre,
                descripcion,
                new ParametrosHerramientaOllama(
                    "object",
                    propiedades,
                    requeridos)));
    }

    private static string ObtenerArgumento(
        LlamadaHerramientaOllama llamada,
        string nombre)
    {
        if (!llamada.Funcion.Argumentos.TryGetValue(
                nombre,
                out JsonElement valor))
        {
            return string.Empty;
        }

        return valor.ValueKind == JsonValueKind.String
            ? (valor.GetString() ?? string.Empty).Trim()
            : valor.ToString().Trim();
    }

    private static bool TryValidarComando(
        string comando,
        bool debeSerConsulta,
        ICollection<ResultadoPasoControl> pasos,
        Action<EventoControl>? informar,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(comando))
        {
            error = debeSerConsulta
                ? "Falta una consulta PowerShell."
                : "Falta el comando PowerShell.";
            return false;
        }

        ResultadoValidacionPowerShell validacion =
            ValidadorPowerShell.Validar(comando);

        if (!validacion.Permitido)
        {
            error = "BLOQUEADO: " + validacion.Motivo;
            var pasoBloqueado = new ResultadoPasoControl(
                pasos.Count + 1,
                comando,
                false,
                -1,
                string.Empty,
                error);
            pasos.Add(pasoBloqueado);
            Informar(
                informar,
                new EventoControl(
                    "bloqueado",
                    error,
                    comando,
                    pasoBloqueado));
            return false;
        }

        bool esConsulta = EsComandoDeConsulta(comando);

        if (debeSerConsulta && !esConsulta)
        {
            error =
                "La propuesta no es una consulta de sólo lectura. "
                + "Propón primero una consulta que no cambie el PC.";
            return false;
        }

        if (!debeSerConsulta && esConsulta)
        {
            error =
                "La propuesta de acción sólo contiene una consulta. "
                + "Propón el comando que realiza la acción.";
            return false;
        }

        return true;
    }

    internal static string NormalizarVerificacionVentanas(
        string comando)
    {
        if (!Regex.IsMatch(
                comando,
                @"\bControlPCIA(?:\.exe)?\s+window\b[^\r\n;]*\s--list\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant))
        {
            return comando;
        }

        int canalizacion = comando.IndexOf('|');
        return canalizacion < 0
            ? comando
            : comando[..canalizacion].TrimEnd();
    }

    private static async Task<ResultadoEjecucionPowerShell>
        EjecutarAsync(
            string comando,
            DependenciasControlWindows dependencias,
            Action<EventoControl>? informar,
            CancellationToken cancellationToken)
    {
        Informar(
            informar,
            new EventoControl(
                "comando",
                "ControlPCIA va a ejecutar el comando validado.",
                comando));

        try
        {
            return await dependencias.EjecutarAsync(
                comando,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ResultadoEjecucionPowerShell(
                false,
                -1,
                string.Empty,
                ex.Message);
        }
    }

    private static ResultadoPasoControl RegistrarPaso(
        string comando,
        ResultadoEjecucionPowerShell ejecucion,
        ICollection<ResultadoPasoControl> pasos,
        Action<EventoControl>? informar)
    {
        var paso = new ResultadoPasoControl(
            pasos.Count + 1,
            comando,
            ejecucion.Ejecutado,
            ejecucion.CodigoSalida,
            ejecucion.Salida,
            ejecucion.Error);
        pasos.Add(paso);
        Informar(
            informar,
            new EventoControl(
                EsPasoCorrecto(paso)
                    ? "resultado"
                    : "error",
                CrearMensajeResultadoBreve(ejecucion),
                comando,
                paso));
        return paso;
    }

    private static bool EsPasoCorrecto(
        ResultadoPasoControl paso)
    {
        return paso.Ejecutado
               && paso.CodigoSalida == 0
               && string.IsNullOrWhiteSpace(paso.Error);
    }

    internal static bool VerificacionDemuestraResultado(
        string comandoAccion,
        string comandoVerificacion,
        string salida)
    {
        if (string.IsNullOrWhiteSpace(salida))
        {
            return false;
        }

        if (!Regex.IsMatch(
                comandoVerificacion,
                @"\bControlPCIA(?:\.exe)?\s+window\b[^\r\n;]*\s--list\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant))
        {
            return true;
        }

        try
        {
            using JsonDocument documento =
                JsonDocument.Parse(salida.Trim());
            JsonElement raiz = documento.RootElement;

            if (!raiz.TryGetProperty(
                    "correcto",
                    out JsonElement correcto)
                || correcto.ValueKind != JsonValueKind.True
                || !raiz.TryGetProperty(
                    "coincidencias",
                    out JsonElement coincidencias)
                || !coincidencias.TryGetInt32(
                    out int cantidad))
            {
                return false;
            }

            if (Regex.IsMatch(
                    comandoAccion,
                    @"\s--close\b",
                    RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant))
            {
                return cantidad == 0;
            }

            if (cantidad <= 0
                || !raiz.TryGetProperty(
                    "ventanas",
                    out JsonElement ventanas)
                || ventanas.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            string? estadoEsperado =
                ObtenerArgumentoConsola(
                    comandoAccion,
                    "state");
            bool exigePrimerPlano =
                Regex.IsMatch(
                    comandoAccion,
                    @"\s--foreground\b",
                    RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);
            int? x = ObtenerEnteroConsola(
                comandoAccion,
                "x");
            int? y = ObtenerEnteroConsola(
                comandoAccion,
                "y");
            int? ancho = ObtenerEnteroConsola(
                comandoAccion,
                "width");
            int? alto = ObtenerEnteroConsola(
                comandoAccion,
                "height");

            return ventanas.EnumerateArray().Any(ventana =>
                (!exigePrimerPlano
                 || (ventana.TryGetProperty(
                         "primerPlano",
                         out JsonElement primerPlano)
                     && primerPlano.ValueKind
                     == JsonValueKind.True))
                && (estadoEsperado is null
                    || (ventana.TryGetProperty(
                            "estado",
                            out JsonElement estado)
                        && string.Equals(
                            estado.GetString(),
                            NormalizarEstadoVentana(
                                estadoEsperado),
                            StringComparison.OrdinalIgnoreCase)))
                && CoincideEntero(
                    ventana,
                    "x",
                    x)
                && CoincideEntero(
                    ventana,
                    "y",
                    y)
                && CoincideEntero(
                    ventana,
                    "ancho",
                    ancho)
                && CoincideEntero(
                    ventana,
                    "alto",
                    alto));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CoincideEntero(
        JsonElement elemento,
        string propiedad,
        int? esperado)
    {
        return esperado is null
               || (elemento.TryGetProperty(
                       propiedad,
                       out JsonElement valor)
                   && valor.TryGetInt32(out int numero)
                   && numero == esperado.Value);
    }

    private static int? ObtenerEnteroConsola(
        string comando,
        string nombre)
    {
        string? valor =
            ObtenerArgumentoConsola(
                comando,
                nombre);
        return int.TryParse(
            valor,
            out int numero)
                ? numero
                : null;
    }

    private static string? ObtenerArgumentoConsola(
        string comando,
        string nombre)
    {
        Match coincidencia = Regex.Match(
            comando,
            $@"\s--{Regex.Escape(nombre)}\s+(?:'(?<valor>[^']+)'|""(?<valor>[^""]+)""|(?<valor>[^\s;|]+))",
            RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant);
        return coincidencia.Success
            ? coincidencia.Groups["valor"].Value
            : null;
    }

    private static string NormalizarEstadoVentana(
        string estado)
    {
        return estado.ToLowerInvariant() switch
        {
            "maximizada" or "maximizado" => "maximized",
            "minimizada" or "minimizado" => "minimized",
            "restored" => "normal",
            _ => estado.ToLowerInvariant()
        };
    }

    private static void AgregarResultadoHerramienta(
        ICollection<MensajeOllama> mensajes,
        string nombreHerramienta,
        bool correcto,
        string detalle)
    {
        mensajes.Add(
            new MensajeOllama(
                "tool",
                JsonSerializer.Serialize(
                    new
                    {
                        correcto,
                        detalle
                    }),
                NombreHerramienta: nombreHerramienta));
    }

    private static string CrearResultadoHerramienta(
        string tipo,
        ResultadoEjecucionPowerShell resultado)
    {
        return JsonSerializer.Serialize(
            new
            {
                tipo,
                ejecutado = resultado.Ejecutado,
                codigo_salida = resultado.CodigoSalida,
                stdout = resultado.Salida,
                stderr = resultado.Error
            });
    }

    private static bool EsResultadoDemasiadoAmplio(
        string salida)
    {
        if (salida.Length > 12_000)
        {
            return true;
        }

        int lineas = 1;

        foreach (char caracter in salida)
        {
            if (caracter == '\n'
                && ++lineas > 80)
            {
                return true;
            }
        }

        return false;
    }

    private static string CrearResultadoDemasiadoAmplio(
        string salida)
    {
        string muestra = string.Join(
            Environment.NewLine,
            salida
                .Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Take(20));

        if (muestra.Length > 3_000)
        {
            muestra = muestra[..3_000];
        }

        return JsonSerializer.Serialize(
            new
            {
                tipo = "consulta_demasiado_amplia",
                mensaje =
                    "ControlPCIA no acepta este volcado como respuesta. Propón una consulta con filtros, sólo los campos necesarios y un máximo de 50 resultados.",
                caracteres = salida.Length,
                muestra
            });
    }

    private static ResultadoControl FinalizarModoPrueba(
        IReadOnlyList<string> comandos,
        IReadOnlyList<ResultadoPasoControl> pasosAnteriores,
        Action<EventoControl>? informar)
    {
        ResultadoPasoControl[] propuestas = comandos
            .Select((comando, indice) =>
                new ResultadoPasoControl(
                    pasosAnteriores.Count + indice + 1,
                    comando,
                    false,
                    0,
                    string.Empty,
                    string.Empty))
            .ToArray();

        return Finalizar(
            false,
            "prueba_sin_ejecucion",
            "Modo de prueba seguro: ControlPCIA validó la propuesta, pero no ejecutó ningún comando.",
            pasosAnteriores.Concat(propuestas).ToArray(),
            false,
            informar);
    }

    private static string CrearMensajeExitoVerificado(
        string evidencia)
    {
        const int maximoCaracteres = 4_000;
        string texto = evidencia.Trim();

        if (texto.Length > maximoCaracteres)
        {
            texto = texto[..maximoCaracteres]
                    + Environment.NewLine
                    + "[Resultado abreviado]";
        }

        return "Tarea completada y comprobada."
               + Environment.NewLine
               + Environment.NewLine
               + texto;
    }

    internal static bool EsComandoDeConsulta(string comando)
    {
        ScriptBlockAst ast = Parser.ParseInput(
            comando,
            out _,
            out ParseError[] errores);

        if (errores.Length > 0
            || ast.FindAll(
                    nodo => nodo is InvokeMemberExpressionAst,
                    searchNestedScriptBlocks: true)
                .Any())
        {
            return false;
        }

        CommandAst[] comandos = ast
            .FindAll(
                nodo => nodo is CommandAst,
                searchNestedScriptBlocks: true)
            .Cast<CommandAst>()
            .ToArray();

        if (comandos.Length == 0)
        {
            return !ast
                .FindAll(
                    nodo => nodo is AssignmentStatementAst,
                    searchNestedScriptBlocks: true)
                .Any();
        }

        return comandos.All(comandoAst =>
            EsNombreComandoDeConsulta(
                comandoAst.GetCommandName(),
                comandoAst.Extent.Text));
    }

    internal static bool EsPeticionDeAccion(string peticion)
    {
        string normalizada = MemoriaRecetas.Normalizar(peticion);

        return Regex.IsMatch(
            normalizada,
            @"\b(?:abre(?:lo|la|los|las)?|abrir|cierra|cerrar|inicia|iniciar|lanza|lanzar|ejecuta|ejecutar|crea|crear|cambia|cambiar|configura|configurar|maximiza|maximizar|minimiza|minimizar|mueve|mover|coloca|colocar|reproduce|reproducir|pon|poner|instala|instalar|descarga|descargar|guarda|guardar|activa|activar|desactiva|desactivar|apaga|apagar|reinicia|reiniciar|enciende|encender|escribe|escribir|haz|hacer|controla|controlar)\b",
            RegexOptions.CultureInvariant);
    }

    internal static bool TryExtraerPregunta(
        string texto,
        out string pregunta)
    {
        pregunta = (texto ?? string.Empty).Trim();

        foreach (string prefijo in
                 new[]
                 {
                     "PREGUNTAR:",
                     "CONFIRMAR:",
                     "RESPONDER:"
                 })
        {
            if (pregunta.StartsWith(
                    prefijo,
                    StringComparison.OrdinalIgnoreCase))
            {
                pregunta = pregunta[prefijo.Length..].Trim();
                break;
            }
        }

        if (!pregunta.EndsWith(
                "?",
                StringComparison.Ordinal))
        {
            pregunta = string.Empty;
            return false;
        }

        string normalizada = MemoriaRecetas.Normalizar(pregunta);
        bool parecePregunta =
            pregunta.StartsWith(
                "¿",
                StringComparison.Ordinal)
            || Regex.IsMatch(
                normalizada,
                @"\b(?:que|cual|cuales|donde|cuando|como|quieres|puedes|necesito|indica|dime)\b",
                RegexOptions.CultureInvariant);

        if (!parecePregunta)
        {
            pregunta = string.Empty;
        }

        return parecePregunta;
    }

    internal static bool EsComandoCompatibleConModoConsola(
        string comando)
    {
        return !Regex.IsMatch(
            comando,
            @"\bControlPCIA(?:\.exe)?\s+(?:ui|window\s+keys)\b|\.SendKeys\s*\(|\bSystem\.Windows\.Forms\.SendKeys\b|\.SendWait\s*\(|\bUIAutomation(?:Client)?\b",
            RegexOptions.IgnoreCase
            | RegexOptions.CultureInvariant);
    }

    internal static bool RequiereVerificacionTrasCambio(
        IReadOnlyList<ResultadoPasoControl> pasos)
    {
        int ultimoCambio = -1;
        int ultimaObservacion = -1;

        for (int indice = 0; indice < pasos.Count; indice++)
        {
            ResultadoPasoControl paso = pasos[indice];

            if (!paso.Ejecutado
                || paso.CodigoSalida != 0
                || !string.IsNullOrWhiteSpace(paso.Error))
            {
                continue;
            }

            if (EsComandoQueCambiaEstado(paso.Comando))
            {
                ultimoCambio = indice;
            }

            if (ultimoCambio >= 0
                && indice > ultimoCambio
                && EsComandoDeConsulta(paso.Comando)
                && (!EsInicioAplicacion(
                        pasos[ultimoCambio].Comando)
                    || !string.IsNullOrWhiteSpace(paso.Salida)))
            {
                ultimaObservacion = indice;
            }
        }

        return ultimoCambio >= 0
               && ultimaObservacion < ultimoCambio;
    }

    private static bool EsComandoQueCambiaEstado(string comando)
    {
        return !EsComandoDeConsulta(comando);
    }

    private static bool EsInicioAplicacion(string comando)
    {
        return Regex.IsMatch(
                   comando,
                   @"(?:^|[;|\r\n]\s*)(?:Start-Process|start|saps)\b",
                   RegexOptions.IgnoreCase
                   | RegexOptions.CultureInvariant)
               || Regex.IsMatch(
                   comando,
                   @"^\s*explorer(?:\.exe)?\s+['""]shell:AppsFolder\\",
                   RegexOptions.IgnoreCase
                   | RegexOptions.CultureInvariant);
    }

    private static bool EsNombreComandoDeConsulta(
        string? nombreOriginal,
        string textoComando)
    {
        if (string.IsNullOrWhiteSpace(nombreOriginal))
        {
            return false;
        }

        string nombre = nombreOriginal.Contains('\\')
            ? nombreOriginal[(nombreOriginal.LastIndexOf('\\') + 1)..]
            : nombreOriginal;
        string nombreSinExe = nombre.EndsWith(
                ".exe",
                StringComparison.OrdinalIgnoreCase)
            ? nombre[..^4]
            : nombre;

        if (nombreSinExe.Equals(
                "winget",
                StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                textoComando,
                @"^\s*winget(?:\.exe)?\s+(?:search|show|list|source\s+list)\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (nombreSinExe.Equals(
                "reg",
                StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                textoComando,
                @"^\s*reg(?:\.exe)?\s+query\b",
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant);
        }

        if (nombreSinExe.Equals(
                "ControlPCIA",
                StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(
                       textoComando,
                       @"\bwindow\b",
                       RegexOptions.IgnoreCase
                       | RegexOptions.CultureInvariant)
                   && Regex.IsMatch(
                       textoComando,
                       @"\s--list\b",
                       RegexOptions.IgnoreCase
                       | RegexOptions.CultureInvariant)
                   && !Regex.IsMatch(
                       textoComando,
                       @"\s--(?:foreground|state|close|x|y|width|height)\b",
                       RegexOptions.IgnoreCase
                       | RegexOptions.CultureInvariant);
        }

        var externos = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "curl",
            "wget",
            "where",
            "tasklist",
            "systeminfo",
            "ipconfig",
            "netstat",
            "whoami",
            "hostname",
            "nslookup",
            "ping"
        };

        if (externos.Contains(nombreSinExe))
        {
            return true;
        }

        var nombres = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Invoke-WebRequest",
            "Invoke-RestMethod",
            "Write-Output",
            "Write-Host",
            "Where-Object",
            "Select-Object",
            "Sort-Object",
            "Group-Object",
            "Measure-Object",
            "Compare-Object",
            "ForEach-Object",
            "Format-List",
            "Format-Table",
            "Out-String"
        };

        if (nombres.Contains(nombre))
        {
            if (nombre.Equals(
                    "Invoke-WebRequest",
                    StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(
                    textoComando,
                    @"\s-OutFile\b",
                    RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant))
            {
                return false;
            }

            return true;
        }

        int separador = nombre.IndexOf('-');
        string verbo = separador > 0
            ? nombre[..separador]
            : string.Empty;
        var verbosConsulta = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Get",
            "Find",
            "Search",
            "Test",
            "Measure",
            "Select",
            "Where",
            "Sort",
            "Group",
            "Compare",
            "Resolve",
            "Read",
            "Format",
            "ConvertFrom"
        };

        return verbosConsulta.Contains(verbo);
    }

    private static async Task<IReadOnlyList<RecetaReferencia>>
        BuscarAprendidosAsync(
            string peticion,
            DependenciasControlWindows dependencias,
            Action<EventoControl>? informar,
            CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<RecetaReferencia> recetas =
                await dependencias.BuscarAprendidoAsync(
                    peticion,
                    cancellationToken);
            RecetaReferencia[] validas = recetas
                .Where(receta =>
                    receta.Comandos.Count > 0
                    && receta.Comandos.All(comando =>
                        ValidadorPowerShell.Validar(comando).Permitido
                        && EsComandoCompatibleConModoConsola(comando)))
                .Take(5)
                .ToArray();

            if (validas.Length > 0)
            {
                Informar(
                    informar,
                    new EventoControl(
                        "memoria",
                        $"Se encontraron {validas.Length} secuencias aprendidas relacionadas."));
            }

            return validas;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Informar(
                informar,
                new EventoControl(
                    "memoria_error",
                    "La memoria local no está disponible; la petición continuará sin ella."));
            return [];
        }
    }

    private static async Task<bool> AprenderAsync(
        string peticion,
        IReadOnlyList<ResultadoPasoControl> pasos,
        DependenciasControlWindows dependencias,
        Action<EventoControl>? informar,
        CancellationToken cancellationToken)
    {
        string[] comandos = pasos
            .Where(paso =>
                paso.Ejecutado
                && paso.CodigoSalida == 0
                && string.IsNullOrWhiteSpace(paso.Error))
            .Select(paso => paso.Comando)
            .ToArray();

        if (comandos.Length == 0)
        {
            return false;
        }

        try
        {
            bool aprendido =
                await dependencias.AprenderAsync(
                    peticion,
                    comandos,
                    cancellationToken);

            if (aprendido)
            {
                Informar(
                    informar,
                    new EventoControl(
                        "aprendido",
                        "Los comandos que funcionaron quedaron guardados en la memoria local."));
            }

            return aprendido;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            Informar(
                informar,
                new EventoControl(
                    "memoria_error",
                    "La orden terminó, pero no se pudo actualizar la memoria local."));
            return false;
        }
    }

    private static async Task<string> ObtenerContextoLocalAsync(
        string peticion,
        DependenciasControlWindows dependencias,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await dependencias.ObtenerContextoLocalAsync(
                    peticion,
                    cancellationToken))
                .Trim();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // El inventario acelera y fundamenta la traducción, pero una
            // consulta local fallida no debe impedir que la IA continúe.
            return string.Empty;
        }
    }

    private static IReadOnlyList<MensajeConversacionControl>
        NormalizarContexto(
            IReadOnlyList<MensajeConversacionControl>? contexto)
    {
        if (contexto is null || contexto.Count == 0)
        {
            return [];
        }

        var resultado = new List<MensajeConversacionControl>();
        int caracteres = 0;

        foreach (MensajeConversacionControl mensaje in contexto
                     .TakeLast(MaximoMensajesContexto))
        {
            string rol = mensaje.Rol.Equals(
                    "assistant",
                    StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : "user";
            string texto = (mensaje.Texto ?? string.Empty).Trim();

            if (texto.Length == 0
                || caracteres + texto.Length > MaximoCaracteresContexto)
            {
                continue;
            }

            resultado.Add(
                new MensajeConversacionControl(
                    rol,
                    texto));
            caracteres += texto.Length;
        }

        return resultado;
    }

    private static string CrearMensajePeticion(
        string peticion,
        IReadOnlyList<RecetaReferencia> aprendidos,
        string contextoLocal)
    {
        var mensaje = new StringBuilder();
        mensaje.AppendLine("PETICIÓN DEL USUARIO:");
        mensaje.AppendLine(peticion);
        mensaje.AppendLine();
        mensaje.AppendLine(
            "DATOS REALES DE APLICACIONES INSTALADAS RELACIONADAS:");
        mensaje.AppendLine(
            contextoLocal.Length == 0
                ? "Ninguno."
                : contextoLocal);
        mensaje.AppendLine();
        mensaje.AppendLine("CAPACIDADES DE CONSOLA VERIFICADAS:");
        mensaje.AppendLine(
            "- Consultar ventanas superiores: ControlPCIA.exe window --list --match '<proceso o título>'.");
        mensaje.AppendLine(
            "- Activar, cambiar estado o colocar una ventana superior: ControlPCIA.exe window --match '<proceso o título>' --foreground --state maximized|normal|minimized; opcionalmente --x N --y N --width N --height N.");
        mensaje.AppendLine(
            "- Solicitar cierre normal, conservando los avisos de trabajo sin guardar: ControlPCIA.exe window --match '<proceso o título>' --close.");
        mensaje.AppendLine(
            "Estos comandos no inspeccionan la pantalla ni usan ratón, teclado, SendKeys o UI Automation.");
        mensaje.AppendLine();
        mensaje.AppendLine("COMANDOS APRENDIDOS RELACIONADOS:");

        if (aprendidos.Count == 0)
        {
            mensaje.Append("Ninguno.");
            return mensaje.ToString();
        }

        int numero = 1;

        foreach (RecetaReferencia receta in aprendidos)
        {
            foreach (string comando in receta.Comandos)
            {
                mensaje.AppendLine($"{numero}. {comando}");
                numero++;
            }
        }

        return mensaje.ToString().TrimEnd();
    }

    private static string CrearMensajeResultadoBreve(
        ResultadoEjecucionPowerShell resultado)
    {
        if (!resultado.Ejecutado)
        {
            return string.IsNullOrWhiteSpace(resultado.Error)
                ? "PowerShell no pudo ejecutar el comando."
                : resultado.Error;
        }

        if (resultado.CodigoSalida != 0
            || !string.IsNullOrWhiteSpace(resultado.Error))
        {
            return CrearMensajeErrorFinal(resultado);
        }

        return string.IsNullOrWhiteSpace(resultado.Salida)
            ? "PowerShell terminó correctamente y no devolvió salida."
            : resultado.Salida;
    }

    private static string CrearMensajeErrorFinal(
        ResultadoEjecucionPowerShell resultado)
    {
        string error = string.IsNullOrWhiteSpace(resultado.Error)
            ? "PowerShell terminó con un error sin texto adicional."
            : resultado.Error;

        return $"Código de salida: {resultado.CodigoSalida}. {error}";
    }

    private static ResultadoControl Finalizar(
        bool completado,
        string estado,
        string mensaje,
        IReadOnlyList<ResultadoPasoControl> pasos,
        bool aprendido,
        Action<EventoControl>? informar)
    {
        Informar(
            informar,
            new EventoControl(
                "final",
                mensaje));

        return new ResultadoControl(
            completado,
            estado,
            mensaje,
            pasos,
            aprendido);
    }

    private static void Informar(
        Action<EventoControl>? informar,
        EventoControl evento)
    {
        informar?.Invoke(evento);
    }
}
