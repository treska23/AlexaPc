using Xunit;

namespace ControlPCIA.Tests;

public sealed class ControlBasicoTests
{
    public static TheoryData<
        string,
        string,
        string> Peticiones => new()
    {
        {
            "abre Cubase",
            "AbrirAplicacion",
            "Cubase"
        },
        {
            "quiero que abras Cubase 15",
            "AbrirAplicacion",
            "Cubase 15"
        },
        {
            "puedes abrir la aplicación Calculadora, por favor",
            "AbrirAplicacion",
            "Calculadora"
        },
        {
            "¿Qué programas tengo abiertos?",
            "ConsultarAplicacionesAbiertas",
            ""
        },
        {
            "dime qué aplicaciones hay en ejecución",
            "ConsultarAplicacionesAbiertas",
            ""
        },
        {
            "dime el nombre de mi ordenador",
            "ConsultarNombreEquipo",
            ""
        }
    };

    [Theory]
    [MemberData(nameof(Peticiones))]
    public void Interpreta_solo_las_dos_capacidades_basicas(
        string texto,
        string tipo,
        string objetivo)
    {
        PeticionBasica resultado =
            ControlBasico.Interpretar(texto);

        Assert.Equal(
            tipo,
            resultado.Tipo.ToString());
        Assert.Equal(objetivo, resultado.Objetivo);
    }

    [Theory]
    [InlineData("abre Cubase y la calculadora")]
    [InlineData("suma dos más cinco")]
    [InlineData("crea un proyecto nuevo")]
    public void Rechaza_lo_que_aun_no_pertenece_al_nucleo(
        string texto)
    {
        PeticionBasica resultado =
            ControlBasico.Interpretar(texto);

        Assert.Equal(
            TipoPeticionBasica.NoCompatible,
            resultado.Tipo);
        Assert.NotEmpty(resultado.Motivo);
    }

    [Fact]
    public async Task Envia_un_unico_comando_para_abrir_la_aplicacion()
    {
        var comandos = new List<string>();
        DependenciasControlBasico dependencias =
            CrearDependencias(
                [
                    new AplicacionInstalada(
                        "Cubase 15",
                        @"{Programs}\Steinberg\Cubase 15\Cubase15.exe")
                ],
                (comando, _) =>
                {
                    comandos.Add(comando);
                    return Task.FromResult(
                        Correcto());
                });

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "abre Cubase",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.True(resultado.Completado);
        Assert.Equal("completado", resultado.Estado);
        Assert.Single(comandos);
        Assert.Single(resultado.Pasos);
        Assert.Contains(
            "shell:AppsFolder",
            comandos[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "Cubase15.exe",
            comandos[0],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Get-Process",
            comandos[0],
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "He enviado a Windows",
            resultado.Mensaje,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traduce_calculadora_a_su_nombre_del_inventario()
    {
        string? comando = null;
        DependenciasControlBasico dependencias =
            CrearDependencias(
                [
                    new AplicacionInstalada(
                        "Calculator",
                        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")
                ],
                (valor, _) =>
                {
                    comando = valor;
                    return Task.FromResult(
                        Correcto());
                });

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "abre la calculadora",
                true,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        Assert.Null(comando);
        Assert.Contains(
            "Microsoft.WindowsCalculator",
            resultado.Pasos[0].Comando,
            StringComparison.Ordinal);
        Assert.Single(resultado.Pasos);
    }

    [Theory]
    [InlineData("abre el explorador de Windows")]
    [InlineData("abre el explorador de archivos")]
    [InlineData("abre el explorador")]
    [InlineData("abre el administrador de archivos")]
    public async Task Abre_el_explorador_y_no_click_to_do(
        string peticion)
    {
        DependenciasControlBasico dependencias =
            CrearDependencias(
                [
                    new AplicacionInstalada(
                        "Click to Do",
                        "MicrosoftWindows.Client.CoreAI_cw5n1h2txyewy!ClickToDoApp"),
                    new AplicacionInstalada(
                        "Explorador de archivos",
                        "Microsoft.Windows.Explorer")
                ],
                (_, _) =>
                    throw new InvalidOperationException(
                        "El modo de prueba no debe ejecutar PowerShell."));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                peticion,
                true,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        Assert.Single(resultado.Pasos);
        Assert.Contains(
            "Microsoft.Windows.Explorer",
            resultado.Pasos[0].Comando,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ClickToDo",
            resultado.Pasos[0].Comando,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Devuelve_el_error_de_powershell_sin_comprobar_la_ventana()
    {
        DependenciasControlBasico dependencias =
            CrearDependencias(
                [
                    new AplicacionInstalada(
                        "Cubase 15",
                        @"Programs\Cubase15.exe")
                ],
                (_, _) => Task.FromResult(
                    new ResultadoEjecucionPowerShell(
                        true,
                        4,
                        string.Empty,
                        "Windows no pudo iniciar la aplicación")));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "abre Cubase",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal(
            "error_al_abrir",
            resultado.Estado);
        Assert.Single(resultado.Pasos);
        Assert.Contains(
            "Windows no pudo iniciar",
            resultado.Mensaje,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Devuelve_las_aplicaciones_abiertas_sin_ia()
    {
        const string json =
            """
            [
              {"Proceso":"Cubase15","Titulo":"Proyecto - Cubase 15","Id":10},
              {"Proceso":"msedge","Titulo":"ControlPCIA - Microsoft Edge","Id":11}
            ]
            """;
        DependenciasControlBasico dependencias =
            CrearDependencias(
                [],
                (_, _) => Task.FromResult(
                    Correcto(json)));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "qué programas tengo abiertos",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.True(resultado.Completado);
        Assert.Equal("respuesta", resultado.Estado);
        Assert.Contains("Cubase15", resultado.Mensaje);
        Assert.Contains("msedge", resultado.Mensaje);
        Assert.Single(resultado.Pasos);
    }

    [Fact]
    public async Task Consulta_el_nombre_del_equipo_con_hostname()
    {
        DependenciasControlBasico dependencias =
            CrearDependencias(
                [],
                (comando, _) =>
                {
                    Assert.Equal("hostname", comando);
                    return Task.FromResult(
                        Correcto("Bardo"));
                });

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "cómo se llama mi PC",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.True(resultado.Completado);
        Assert.Equal("respuesta", resultado.Estado);
        Assert.Contains("Bardo", resultado.Mensaje);
        Assert.Equal(
            "hostname",
            Assert.Single(resultado.Pasos).Comando);
    }

    [Fact]
    public async Task Una_peticion_no_compatible_no_ejecuta_nada()
    {
        DependenciasControlBasico dependencias =
            CrearDependencias(
                [],
                (_, _) =>
                    throw new InvalidOperationException(
                        "No debe ejecutar PowerShell."));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "crea una pista en Cubase",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal("no_disponible", resultado.Estado);
        Assert.Empty(resultado.Pasos);
    }

    private static DependenciasControlBasico CrearDependencias(
        IReadOnlyList<AplicacionInstalada> aplicaciones,
        Func<
            string,
            CancellationToken,
            Task<ResultadoEjecucionPowerShell>> ejecutar)
    {
        return new DependenciasControlBasico(
            _ => Task.FromResult(aplicaciones),
            ejecutar);
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
