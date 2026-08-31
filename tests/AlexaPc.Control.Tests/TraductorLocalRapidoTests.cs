using System.Text.Json;
using Xunit;

namespace ControlPCIA.Tests;

public sealed class TraductorLocalRapidoTests
{
    [Fact]
    public void El_modelo_solo_traduce_y_recibe_las_tres_restricciones()
    {
        Assert.Contains(
            "Tú no ejecutas nada",
            TraductorLocalRapido.InstruccionSistema,
            StringComparison.Ordinal);
        Assert.Contains(
            "No eliminar",
            TraductorLocalRapido.InstruccionSistema,
            StringComparison.Ordinal);
        Assert.Contains(
            "No mover ni cortar",
            TraductorLocalRapido.InstruccionSistema,
            StringComparison.Ordinal);
        Assert.Contains(
            "No formatear",
            TraductorLocalRapido.InstruccionSistema,
            StringComparison.Ordinal);
        Assert.Contains(
            "Abrir o crear archivos",
            TraductorLocalRapido.InstruccionSistema,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Una_llamada_traduce_y_el_modo_prueba_no_ejecuta()
    {
        int llamadas = 0;
        int ejecuciones = 0;
        DependenciasTraductorLocal dependencias =
            CrearDependencias(
                (_, _, _) =>
                {
                    llamadas++;
                    return Task.FromResult(
                        RespuestaHerramienta(
                            "proponer_accion",
                            "comando",
                            "Start-Process notepad.exe"));
                },
                (_, _) =>
                {
                    ejecuciones++;
                    return Task.FromResult(Correcto());
                });

        ResultadoControl resultado =
            await TraductorLocalRapido
                .ControlarConDependenciasAsync(
                    "haz algo que el núcleo no conoce",
                    null,
                    true,
                    dependencias,
                    TestContext.Current.CancellationToken);

        Assert.Equal(1, llamadas);
        Assert.Equal(0, ejecuciones);
        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        Assert.Equal(
            "Start-Process notepad.exe",
            Assert.Single(resultado.Pasos).Comando);
    }

    [Fact]
    public async Task Ejecuta_la_consulta_y_devuelve_su_salida()
    {
        bool aprendio = false;
        DependenciasTraductorLocal dependencias =
            CrearDependencias(
                (_, _, _) => Task.FromResult(
                    RespuestaHerramienta(
                        "proponer_consulta",
                        "comando",
                        "Get-Process | Select-Object -First 5 ProcessName")),
                (_, _) => Task.FromResult(
                    Correcto("Cubase15\nmsedge")),
                aprender: (_, _, consulta, _) =>
                {
                    aprendio = consulta;
                    return Task.FromResult(true);
                });

        ResultadoControl resultado =
            await TraductorLocalRapido
                .ControlarConDependenciasAsync(
                    "qué procesos importantes tengo",
                    null,
                    false,
                    dependencias,
                    TestContext.Current.CancellationToken);

        Assert.True(resultado.Completado);
        Assert.Equal("respuesta", resultado.Estado);
        Assert.Equal("Cubase15\nmsedge", resultado.Mensaje);
        Assert.True(aprendio);
        Assert.True(resultado.Aprendido);
    }

    [Fact]
    public async Task Bloquea_una_propuesta_que_elimina_sin_ejecutarla()
    {
        int ejecuciones = 0;
        DependenciasTraductorLocal dependencias =
            CrearDependencias(
                (_, _, _) => Task.FromResult(
                    RespuestaHerramienta(
                        "proponer_accion",
                        "comando",
                        "Remove-Item 'C:\\datos.txt'")),
                (_, _) =>
                {
                    ejecuciones++;
                    return Task.FromResult(Correcto());
                });

        ResultadoControl resultado =
            await TraductorLocalRapido
                .ControlarConDependenciasAsync(
                    "borra datos",
                    null,
                    false,
                    dependencias,
                    TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal("comando_bloqueado", resultado.Estado);
        Assert.Equal(0, ejecuciones);
    }

    [Theory]
    [InlineData(
        "crea una carpeta en el escritorio",
        "New-Item -Path \"$env:SystemRoot\\Desktop\\Prueba\" -ItemType Directory")]
    [InlineData(
        "crea un proyecto nuevo en Cubase",
        "New-Item -Path 'Proyecto.cprj' -ItemType File")]
    public async Task Bloquea_rutas_o_proyectos_inventados(
        string peticion,
        string comando)
    {
        int ejecuciones = 0;
        DependenciasTraductorLocal dependencias =
            CrearDependencias(
                (_, _, _) => Task.FromResult(
                    RespuestaHerramienta(
                        "proponer_accion",
                        "comando",
                        comando)),
                (_, _) =>
                {
                    ejecuciones++;
                    return Task.FromResult(Correcto());
                });

        ResultadoControl resultado =
            await TraductorLocalRapido
                .ControlarConDependenciasAsync(
                    peticion,
                    null,
                    false,
                    dependencias,
                    TestContext.Current.CancellationToken);

        Assert.Equal("comando_bloqueado", resultado.Estado);
        Assert.Equal(0, ejecuciones);
    }

    [Fact]
    public async Task Entrega_contexto_y_puede_preguntar_al_usuario()
    {
        IReadOnlyList<MensajeOllama>? recibidos = null;
        DependenciasTraductorLocal dependencias =
            CrearDependencias(
                (mensajes, _, _) =>
                {
                    recibidos = mensajes;
                    return Task.FromResult(
                        RespuestaHerramienta(
                            "preguntar_usuario",
                            "pregunta",
                            "¿Qué proyecto quieres abrir?"));
                });

        ResultadoControl resultado =
            await TraductorLocalRapido
                .ControlarConDependenciasAsync(
                    "abre ese",
                    [
                        new MensajeConversacionControl(
                            "user",
                            "Tengo dos proyectos de Cubase."),
                        new MensajeConversacionControl(
                            "assistant",
                            "¿Cuál quieres abrir?")
                    ],
                    false,
                    dependencias,
                    TestContext.Current.CancellationToken);

        Assert.NotNull(recibidos);
        Assert.Contains(
            recibidos,
            mensaje =>
                mensaje.Contenido.Contains(
                    "dos proyectos",
                    StringComparison.Ordinal));
        Assert.Equal(
            "requiere_aclaracion",
            resultado.Estado);
        Assert.Contains(
            "Qué proyecto",
            resultado.Mensaje,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Una_traduccion_exacta_aprendida_evitar_consultar_al_modelo()
    {
        DependenciasTraductorLocal dependencias =
            CrearDependencias(
                (_, _, _) =>
                    throw new InvalidOperationException(
                        "No debe consultar al modelo."),
                buscar: (_, _) => Task.FromResult<
                    IReadOnlyList<TraduccionAprendida>>(
                    [
                        new TraduccionAprendida(
                            "consulta el nombre del pc",
                            "hostname",
                            true,
                            3,
                            1)
                    ]));

        ResultadoControl resultado =
            await TraductorLocalRapido
                .ControlarConDependenciasAsync(
                    "consulta el nombre del PC",
                    null,
                    true,
                    dependencias,
                    TestContext.Current.CancellationToken);

        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        Assert.Equal(
            "hostname",
            Assert.Single(resultado.Pasos).Comando);
        Assert.True(resultado.Aprendido);
    }

    private static DependenciasTraductorLocal CrearDependencias(
        Func<
            IReadOnlyList<MensajeOllama>,
            IReadOnlyList<HerramientaOllama>,
            CancellationToken,
            Task<MensajeOllama>> conversar,
        Func<
            string,
            CancellationToken,
            Task<ResultadoEjecucionPowerShell>>? ejecutar = null,
        Func<
            string,
            CancellationToken,
            Task<IReadOnlyList<TraduccionAprendida>>>? buscar = null,
        Func<
            string,
            string,
            bool,
            CancellationToken,
            Task<bool>>? aprender = null)
    {
        return new DependenciasTraductorLocal(
            conversar,
            ejecutar
            ?? ((_, _) => Task.FromResult(Correcto())),
            (_, _) => Task.FromResult<
                IReadOnlyList<string>>([]),
            buscar
            ?? ((_, _) => Task.FromResult<
                IReadOnlyList<TraduccionAprendida>>([])),
            aprender
            ?? ((_, _, _, _) => Task.FromResult(false)));
    }

    private static MensajeOllama RespuestaHerramienta(
        string herramienta,
        string propiedad,
        string valor)
    {
        return new MensajeOllama(
            "assistant",
            string.Empty,
            [
                new LlamadaHerramientaOllama(
                    "function",
                    new FuncionLlamadaOllama(
                        herramienta,
                        new Dictionary<string, JsonElement>
                        {
                            [propiedad] =
                                JsonSerializer
                                    .SerializeToElement(
                                        valor)
                        }))
            ]);
    }

    private static ResultadoEjecucionPowerShell Correcto(
        string salida = "")
    {
        return new ResultadoEjecucionPowerShell(
            true,
            0,
            salida,
            string.Empty);
    }
}
