using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPCIA;

internal enum TipoPeticionBasica
{
    NoCompatible,
    AbrirAplicacion,
    ConsultarAplicacionesAbiertas,
    ConsultarNombreEquipo,
    AbrirPaginaWeb,
    BuscarEnInternet,
    GestionarVentanas,
    GestionarPantallas,
    ControlarMultimedia
}

internal sealed record PeticionBasica(
    TipoPeticionBasica Tipo,
    string Objetivo = "",
    string Motivo = "",
    string Descripcion = "");

internal sealed record DependenciasControlBasico(
    Func<
        CancellationToken,
        Task<IReadOnlyList<AplicacionInstalada>>>
        ObtenerAplicacionesAsync,
    Func<
        string,
        CancellationToken,
        Task<ResultadoEjecucionPowerShell>>
        EjecutarAsync);

/// <summary>
/// Núcleo estable de ControlPCIA. No consulta a un modelo. Traduce primero
/// todas las acciones reconocidas de una petición y después las ejecuta en
/// orden: aplicaciones, consultas, web, ventanas, pantallas y multimedia.
/// </summary>
internal static class ControlBasico
{
    private const string MensajeCapacidades =
        "Puedo abrir una aplicación o página web, buscar en Internet, decirte qué aplicaciones están abiertas, controlar sus ventanas, configurar las pantallas y controlar la reproducción multimedia.";

    private static readonly IReadOnlyDictionary<string, string>
        AliasAplicaciones =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["calculadora"] = "Calculator",
                ["la calculadora"] = "Calculator",
                ["bloc de notas"] = "Notepad",
                ["el bloc de notas"] = "Notepad",
                ["explorador"] = "Explorador de archivos",
                ["explorador de windows"] = "Explorador de archivos",
                ["explorador de archivos"] = "Explorador de archivos",
                ["explorador de ficheros"] = "Explorador de archivos",
                ["administrador de archivos"] = "Explorador de archivos"
            };

    private static readonly Regex VerboAbrir = new(
        @"\b(?:abre(?:me)?|abrir|abras|inicia|iniciar|arranca|arrancar|ejecuta|ejecutar|lanza|lanzar)\b",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex PrefijoPermitido = new(
        @"^(?:por favor[\s,]*|quiero\s+|quiero\s+que\s+|puedes\s+|podrias\s+|podrías\s+|necesito\s+|me\s+)*$",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private static readonly Regex SeparadorAcciones = new(
        @"\s*(?:;|\b(?:y\s+)?(?:después|despues|luego|a\s+continuación|a\s+continuacion)\b)\s*|\s+y\s+(?=(?:abre(?:me)?|abrir|inicia|arranca|ejecuta|lanza|busca|buscar|entra|visita|ve|pon|poner|haz|elige|establece|configura|cambia|convierte|usa|desactiva|deshabilita|activa|reactiva|conecta|desconecta|apaga|enciende|duplica|clona|replica|extiende|coloca|mueve|gira|pausa|para|reanuda|reproduce|maximiza|minimiza|restaura|trae|lleva|cierra|sal|dime|muestra|consulta)\b)",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    public static EstadoControlBasico Estado { get; } =
        new(
            true,
            "control-basico",
            "Control básico preparado: aplicaciones, web, consultas, pantallas y reproducción multimedia.");

    public static Task<ResultadoControl> ControlarAsync(
        string instruccion,
        CancellationToken cancellationToken = default,
        bool soloTraducir = false)
    {
        var dependencias = new DependenciasControlBasico(
            InventarioAplicaciones.ObtenerAplicacionesAsync,
            static (comando, cancelacion) =>
                EjecutorPowerShell.EjecutarAsync(
                    comando,
                    cancelacion));

        return ControlarConDependenciasAsync(
            instruccion,
            soloTraducir,
            dependencias,
            cancellationToken);
    }

    internal static async Task<ResultadoControl>
        ControlarConDependenciasAsync(
            string instruccion,
            bool soloTraducir,
            DependenciasControlBasico dependencias,
            CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> acciones =
            DividirAcciones(instruccion);

        if (acciones.Count <= 1)
        {
            return await EjecutarPeticionAsync(
                Interpretar(instruccion),
                soloTraducir,
                dependencias,
                cancellationToken);
        }

        var preparadas =
            new List<ResultadoControl>(acciones.Count);

        for (int indice = 0; indice < acciones.Count; indice++)
        {
            ResultadoControl preparacion =
                await EjecutarPeticionAsync(
                    Interpretar(acciones[indice]),
                    true,
                    dependencias,
                    cancellationToken);

            if (preparacion.Estado
                != "prueba_sin_ejecucion")
            {
                return Error(
                    preparacion.Estado,
                    $"No he ejecutado ninguna acción. No puedo preparar la acción {indice + 1} («{acciones[indice]}»): {preparacion.Mensaje}");
            }

            preparadas.Add(preparacion);
        }

        if (soloTraducir)
        {
            return Combinar(
                preparadas,
                false,
                "prueba_sin_ejecucion",
                $"He preparado {preparadas.Count} acciones en orden; el modo de prueba no las ejecuta.");
        }

        var resultados =
            new List<ResultadoControl>(acciones.Count);

        for (int indice = 0; indice < acciones.Count; indice++)
        {
            ResultadoControl resultado =
                await EjecutarPeticionAsync(
                    Interpretar(acciones[indice]),
                    false,
                    dependencias,
                    cancellationToken);
            resultados.Add(resultado);

            if (!resultado.Completado)
            {
                return Combinar(
                    resultados,
                    false,
                    resultado.Estado,
                    $"La acción {indice + 1} no se ha completado: {resultado.Mensaje} No he ejecutado las acciones posteriores.");
            }
        }

        return Combinar(
            resultados,
            true,
            "completado",
            string.Join(
                Environment.NewLine,
                resultados.Select(
                    (resultado, indice) =>
                        $"{indice + 1}. {resultado.Mensaje}")));
    }

    private static async Task<ResultadoControl>
        EjecutarPeticionAsync(
            PeticionBasica peticion,
            bool soloTraducir,
            DependenciasControlBasico dependencias,
            CancellationToken cancellationToken)
    {

        return peticion.Tipo switch
        {
            TipoPeticionBasica.AbrirAplicacion =>
                await AbrirAplicacionAsync(
                    peticion.Objetivo,
                    soloTraducir,
                    dependencias,
                    cancellationToken),

            TipoPeticionBasica.ConsultarAplicacionesAbiertas =>
                await ConsultarAplicacionesAbiertasAsync(
                    soloTraducir,
                    dependencias,
                    cancellationToken),

            TipoPeticionBasica.ConsultarNombreEquipo =>
                await ConsultarNombreEquipoAsync(
                    soloTraducir,
                    dependencias,
                    cancellationToken),

            TipoPeticionBasica.AbrirPaginaWeb
                or TipoPeticionBasica.BuscarEnInternet =>
                await ControlWebBasico.EjecutarAsync(
                    peticion,
                    soloTraducir,
                    dependencias,
                    cancellationToken),

            TipoPeticionBasica.GestionarPantallas =>
                await ControlPantallasBasico.EjecutarAsync(
                    peticion,
                    soloTraducir,
                    dependencias,
                    cancellationToken),

            TipoPeticionBasica.GestionarVentanas =>
                await ControlVentanasBasico.EjecutarAsync(
                    peticion,
                    soloTraducir,
                    dependencias,
                    cancellationToken),

            TipoPeticionBasica.ControlarMultimedia =>
                await ControlMultimediaBasico.EjecutarAsync(
                    peticion,
                    soloTraducir,
                    dependencias,
                    cancellationToken),

            _ =>
                NoCompatible(
                    peticion.Motivo)
        };
    }

    internal static IReadOnlyList<string> DividirAcciones(
        string instruccion)
    {
        string texto =
            (instruccion ?? string.Empty).Trim();

        if (texto.Length == 0)
        {
            return [string.Empty];
        }

        string[] partes =
            SeparadorAcciones.Split(texto);

        if (partes.Length <= 1)
        {
            return [texto];
        }

        string[] acciones = partes
            .Select(parte => parte.Trim(' ', ',', '.'))
            .Where(parte => parte.Length > 0)
            .ToArray();
        return acciones.Length > 0
            ? acciones
            : [texto];
    }

    private static ResultadoControl Combinar(
        IReadOnlyList<ResultadoControl> resultados,
        bool completado,
        string estado,
        string mensaje)
    {
        var pasos =
            new List<ResultadoPasoControl>();

        foreach (ResultadoControl resultado in resultados)
        {
            foreach (ResultadoPasoControl paso in resultado.Pasos)
            {
                pasos.Add(
                    paso with
                    {
                        Numero = pasos.Count + 1
                    });
            }
        }

        return new ResultadoControl(
            completado,
            estado,
            mensaje,
            pasos,
            resultados.Any(resultado => resultado.Aprendido));
    }

    internal static PeticionBasica Interpretar(
        string instruccion)
    {
        string texto = (instruccion ?? string.Empty).Trim();
        texto = texto.TrimStart('¿', '¡').TrimStart();

        if (texto.Length == 0)
        {
            return new PeticionBasica(
                TipoPeticionBasica.NoCompatible,
                Motivo: "No he recibido ninguna orden. "
                        + MensajeCapacidades);
        }

        string normalizada = Normalizar(texto);

        if (EsConsultaAplicacionesAbiertas(normalizada))
        {
            return new PeticionBasica(
                TipoPeticionBasica
                    .ConsultarAplicacionesAbiertas);
        }

        if (Regex.IsMatch(
                normalizada,
                @"\b(?:nombre|como\s+se\s+llama)\b.*\b(?:mi\s+)?(?:ordenador|computadora|pc|equipo|host)\b|\bhostname\b",
                RegexOptions.CultureInvariant))
        {
            return new PeticionBasica(
                TipoPeticionBasica
                    .ConsultarNombreEquipo);
        }

        PeticionBasica? peticionMultimedia =
            ControlMultimediaBasico.Interpretar(texto);

        if (peticionMultimedia is not null)
        {
            return peticionMultimedia;
        }

        PeticionBasica? peticionPantallas =
            ControlPantallasBasico.Interpretar(texto);

        if (peticionPantallas is not null)
        {
            return peticionPantallas;
        }

        PeticionBasica? peticionVentanas =
            ControlVentanasBasico.Interpretar(texto);

        if (peticionVentanas is not null)
        {
            return peticionVentanas;
        }

        PeticionBasica? peticionWeb =
            ControlWebBasico.Interpretar(texto);

        if (peticionWeb is not null)
        {
            return peticionWeb;
        }

        Match verbo = VerboAbrir.Match(texto);

        if (!verbo.Success
            || !PrefijoPermitido.IsMatch(
                texto[..verbo.Index]))
        {
            return new PeticionBasica(
                TipoPeticionBasica.NoCompatible,
                Motivo: MensajeCapacidades);
        }

        string objetivo = texto[(verbo.Index + verbo.Length)..]
            .Trim();
        objetivo = Regex.Replace(
                objetivo,
                @"^(?:(?:la|el|una|un)\s+)?(?:(?:aplicacion|aplicación|programa)\s+)?",
                string.Empty,
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant)
            .Trim();
        objetivo = Regex.Replace(
                objetivo,
                @"[\s,.]*(?:por favor)?[.!?]*$",
                string.Empty,
                RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant)
            .Trim();

        if (objetivo.Length == 0)
        {
            return new PeticionBasica(
                TipoPeticionBasica.NoCompatible,
                Motivo:
                    "Dime qué aplicación quieres abrir.");
        }

        string objetivoNormalizado =
            Normalizar(objetivo);

        if (Regex.IsMatch(
                objetivoNormalizado,
                @"(?:[,;]|\s+y\s+|\s+despues\s+|\s+luego\s+)",
                RegexOptions.CultureInvariant)
            || VerboAbrir.IsMatch(objetivo))
        {
            return new PeticionBasica(
                TipoPeticionBasica.NoCompatible,
                Motivo:
                    "En esta primera versión sólo puedo abrir una aplicación cada vez.");
        }

        return new PeticionBasica(
            TipoPeticionBasica.AbrirAplicacion,
            objetivo);
    }

    private static bool EsConsultaAplicacionesAbiertas(
        string texto)
    {
        bool mencionaApertura =
            Regex.IsMatch(
                texto,
                @"\b(?:abierto|abierta|abiertos|abiertas|ejecutando|en ejecucion)\b",
                RegexOptions.CultureInvariant);
        bool mencionaObjetos =
            Regex.IsMatch(
                texto,
                @"\b(?:aplicacion|aplicaciones|programa|programas|ventana|ventanas|cosas|procesos)\b",
                RegexOptions.CultureInvariant);
        bool preguntaPorLoPropio =
            Regex.IsMatch(
                texto,
                @"\b(?:que|cuales|dime|lista|muestra|tengo|hay)\b",
                RegexOptions.CultureInvariant);

        return mencionaApertura
               && (mencionaObjetos
                   || preguntaPorLoPropio)
               && preguntaPorLoPropio;
    }

    private static async Task<ResultadoControl>
        AbrirAplicacionAsync(
            string objetivo,
            bool soloTraducir,
            DependenciasControlBasico dependencias,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<AplicacionInstalada> aplicaciones;

        try
        {
            aplicaciones =
                await dependencias.ObtenerAplicacionesAsync(
                    cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            return Error(
                "inventario_no_disponible",
                "No he podido consultar las aplicaciones instaladas: "
                + ex.Message);
        }

        string busqueda =
            ResolverAlias(objetivo);
        AplicacionInstalada? aplicacion =
            InventarioAplicaciones
                .SeleccionarCandidatas(
                    busqueda,
                    aplicaciones)
                .FirstOrDefault();

        if (aplicacion is null)
        {
            return Error(
                "aplicacion_no_encontrada",
                $"No encuentro «{objetivo}» entre las aplicaciones instaladas.");
        }

        string comando =
            "Start-Process -FilePath 'explorer.exe' -ArgumentList "
            + EscaparLiteralPowerShell(
                "shell:AppsFolder\\"
                + aplicacion.AppId);

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                $"Abriría {aplicacion.Nombre}, pero el modo de prueba no ejecuta comandos.",
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

        ResultadoEjecucionPowerShell lanzamiento =
            await dependencias.EjecutarAsync(
                comando,
                cancellationToken);
        var pasos = new List<ResultadoPasoControl>
        {
            CrearPaso(
                1,
                comando,
                lanzamiento)
        };

        if (!EsCorrecto(lanzamiento))
        {
            return Error(
                "error_al_abrir",
                $"Windows no ha podido abrir {aplicacion.Nombre}: "
                + ObtenerError(lanzamiento),
                pasos);
        }

        string mensaje = string.IsNullOrWhiteSpace(
            lanzamiento.Salida)
            ? $"He enviado a Windows la orden para abrir {aplicacion.Nombre}."
            : $"Windows ha respondido al intentar abrir {aplicacion.Nombre}:\n"
              + lanzamiento.Salida.Trim();

        return new ResultadoControl(
            true,
            "completado",
            mensaje,
            pasos,
            false);
    }

    private static async Task<ResultadoControl>
        ConsultarAplicacionesAbiertasAsync(
            bool soloTraducir,
            DependenciasControlBasico dependencias,
            CancellationToken cancellationToken)
    {
        const string comando =
            "Get-Process | "
            + "Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | "
            + "Sort-Object ProcessName,Id | "
            + "Select-Object -First 50 "
            + "@{Name='Proceso';Expression={$_.ProcessName}},"
            + "@{Name='Titulo';Expression={$_.MainWindowTitle}},Id | "
            + "ConvertTo-Json -Compress";

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                "Consultaría las aplicaciones abiertas, pero el modo de prueba no ejecuta comandos.",
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
            CrearPaso(
                1,
                comando,
                ejecucion);

        if (!EsCorrecto(ejecucion))
        {
            return Error(
                "consulta_fallida",
                "No he podido consultar las aplicaciones abiertas: "
                + ObtenerError(ejecucion),
                [paso]);
        }

        IReadOnlyList<ProcesoVentana> procesos =
            DeserializarProcesos(
                ejecucion.Salida);

        if (procesos.Count == 0)
        {
            return new ResultadoControl(
                true,
                "respuesta",
                "No hay ninguna aplicación con una ventana abierta.",
                [paso],
                false);
        }

        var mensaje = new StringBuilder();
        mensaje.AppendLine(
            procesos.Count == 1
                ? "Tienes una aplicación abierta:"
                : $"Tienes {procesos.Count} aplicaciones abiertas:");

        foreach (ProcesoVentana proceso in procesos)
        {
            mensaje.Append("- ");
            mensaje.Append(proceso.Proceso);

            if (!string.IsNullOrWhiteSpace(proceso.Titulo)
                && !proceso.Titulo.Equals(
                    proceso.Proceso,
                    StringComparison.OrdinalIgnoreCase))
            {
                mensaje.Append(" — ");
                mensaje.Append(proceso.Titulo);
            }

            mensaje.AppendLine();
        }

        return new ResultadoControl(
            true,
            "respuesta",
            mensaje.ToString().TrimEnd(),
            [paso],
            false);
    }

    private static async Task<ResultadoControl>
        ConsultarNombreEquipoAsync(
            bool soloTraducir,
            DependenciasControlBasico dependencias,
            CancellationToken cancellationToken)
    {
        const string comando = "hostname";

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                "Consultaría el nombre del equipo; el modo de prueba no ejecuta comandos.",
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
            CrearPaso(
                1,
                comando,
                ejecucion);

        if (!EsCorrecto(ejecucion))
        {
            return Error(
                "consulta_fallida",
                "No he podido consultar el nombre del equipo: "
                + ObtenerError(ejecucion),
                [paso]);
        }

        string nombre =
            ejecucion.Salida.Trim();
        return new ResultadoControl(
            true,
            "respuesta",
            nombre.Length > 0
                ? $"El equipo se llama {nombre}."
                : "Windows no ha devuelto el nombre del equipo.",
            [paso],
            false);
    }

    private static string ResolverAlias(
        string objetivo)
    {
        string normalizado = Normalizar(objetivo);
        return AliasAplicaciones.TryGetValue(
            normalizado,
            out string? alias)
                ? alias
                : objetivo;
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

    private static ResultadoPasoControl CrearPaso(
        int numero,
        string comando,
        ResultadoEjecucionPowerShell ejecucion)
    {
        return new ResultadoPasoControl(
            numero,
            comando,
            ejecucion.Ejecutado,
            ejecucion.CodigoSalida,
            ejecucion.Salida,
            ejecucion.Error);
    }

    private static bool EsCorrecto(
        ResultadoEjecucionPowerShell resultado)
    {
        return resultado.Ejecutado
               && resultado.CodigoSalida == 0
               && string.IsNullOrWhiteSpace(
                   resultado.Error);
    }

    private static string ObtenerError(
        ResultadoEjecucionPowerShell resultado)
    {
        return string.IsNullOrWhiteSpace(resultado.Error)
            ? $"código de salida {resultado.CodigoSalida}"
            : resultado.Error;
    }

    private static ResultadoControl NoCompatible(
        string motivo)
    {
        return Error(
            "no_disponible",
            string.IsNullOrWhiteSpace(motivo)
                ? MensajeCapacidades
                : motivo);
    }

    private static ResultadoControl Error(
        string estado,
        string mensaje,
        IReadOnlyList<ResultadoPasoControl>? pasos = null)
    {
        return new ResultadoControl(
            false,
            estado,
            mensaje,
            pasos ?? [],
            false);
    }

    private static IReadOnlyList<ProcesoVentana>
        DeserializarProcesos(
            string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument documento =
                JsonDocument.Parse(json);
            IEnumerable<JsonElement> elementos =
                documento.RootElement.ValueKind
                == JsonValueKind.Array
                    ? documento.RootElement
                        .EnumerateArray()
                    : [documento.RootElement];

            return elementos
                .Select(elemento =>
                    new ProcesoVentana(
                        ObtenerTexto(
                            elemento,
                            "Proceso"),
                        ObtenerTexto(
                            elemento,
                            "Titulo"),
                        elemento.TryGetProperty(
                            "Id",
                            out JsonElement id)
                            && id.TryGetInt32(
                                out int numero)
                                ? numero
                                : 0))
                .Where(proceso =>
                    proceso.Proceso.Length > 0)
                .DistinctBy(proceso =>
                    proceso.Proceso
                    + "\n"
                    + proceso.Titulo,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ObtenerTexto(
        JsonElement elemento,
        string propiedad)
    {
        return elemento.TryGetProperty(
                   propiedad,
                   out JsonElement valor)
               && valor.ValueKind
               == JsonValueKind.String
            ? (valor.GetString() ?? string.Empty)
                .Trim()
            : string.Empty;
    }

    internal static string Normalizar(
        string texto)
    {
        string descompuesto = (texto ?? string.Empty)
            .Normalize(NormalizationForm.FormD);
        var resultado = new StringBuilder(
            descompuesto.Length);

        foreach (char caracter in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(
                    caracter)
                != UnicodeCategory.NonSpacingMark)
            {
                resultado.Append(
                    char.ToLowerInvariant(caracter));
            }
        }

        return resultado
            .ToString()
            .Normalize(NormalizationForm.FormC);
    }

    private sealed record ProcesoVentana(
        string Proceso,
        string Titulo,
        int Id);
}
