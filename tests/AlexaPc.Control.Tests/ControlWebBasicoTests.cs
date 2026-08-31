using Xunit;

namespace ControlPCIA.Tests;

public sealed class ControlWebBasicoTests
{
    public static TheoryData<
        string,
        string,
        string> PeticionesWeb => new()
    {
        {
            "abre YouTube",
            "AbrirPaginaWeb",
            "https://www.youtube.com/"
        },
        {
            "abre la página de YouTube",
            "AbrirPaginaWeb",
            "https://www.youtube.com/"
        },
        {
            "entra en youtube.com",
            "AbrirPaginaWeb",
            "https://youtube.com/"
        },
        {
            "abre https://openai.com/research/",
            "AbrirPaginaWeb",
            "https://openai.com/research/"
        },
        {
            "busca baterías electrónicas por internet",
            "BuscarEnInternet",
            "https://www.google.com/search?q=baterias%20electronicas"
        },
        {
            "busca en YouTube drumless para batería",
            "BuscarEnInternet",
            "https://www.youtube.com/results?search_query=drumless%20para%20bateria"
        },
        {
            "busca drumless en YouTube",
            "BuscarEnInternet",
            "https://www.youtube.com/results?search_query=drumless"
        },
        {
            "búscame por internet noticias de música",
            "BuscarEnInternet",
            "https://www.google.com/search?q=noticias%20de%20musica"
        },
        {
            "quiero que busques en Google información sobre ControlPCIA",
            "BuscarEnInternet",
            "https://www.google.com/search?q=informacion%20sobre%20controlpcia"
        }
    };

    [Theory]
    [MemberData(nameof(PeticionesWeb))]
    public void Interpreta_paginas_y_busquedas(
        string texto,
        string tipo,
        string url)
    {
        PeticionBasica resultado =
            ControlBasico.Interpretar(texto);

        Assert.Equal(
            tipo,
            resultado.Tipo.ToString());
        Assert.Equal(url, resultado.Objetivo);
    }

    [Theory]
    [InlineData("abre Cubase")]
    [InlineData("abre el explorador de Windows")]
    public void No_confunde_aplicaciones_con_paginas(
        string texto)
    {
        PeticionBasica resultado =
            ControlBasico.Interpretar(texto);

        Assert.Equal(
            TipoPeticionBasica.AbrirAplicacion,
            resultado.Tipo);
    }

    [Fact]
    public async Task Envia_una_url_al_navegador_predeterminado_una_sola_vez()
    {
        var comandos = new List<string>();
        DependenciasControlBasico dependencias =
            CrearDependencias(
                (comando, _) =>
                {
                    comandos.Add(comando);
                    return Task.FromResult(
                        Correcto());
                });

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "abre YouTube",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.True(resultado.Completado);
        Assert.Equal("completado", resultado.Estado);
        string comando = Assert.Single(comandos);
        Assert.Equal(
            "Start-Process -FilePath 'https://www.youtube.com/'",
            comando);
        Assert.True(
            ValidadorPowerShell.Validar(comando).Permitido);
        Assert.Single(resultado.Pasos);
        Assert.Contains(
            "navegador predeterminado",
            resultado.Mensaje,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task El_modo_seguro_no_abre_el_navegador()
    {
        DependenciasControlBasico dependencias =
            CrearDependencias(
                (_, _) =>
                    throw new InvalidOperationException(
                        "No debe ejecutar el comando."));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "busca ControlPCIA en internet",
                true,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        Assert.Single(resultado.Pasos);
        Assert.Contains(
            "google.com/search",
            resultado.Pasos[0].Comando,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Devuelve_el_error_de_windows_sin_comprobar_el_navegador()
    {
        DependenciasControlBasico dependencias =
            CrearDependencias(
                (_, _) => Task.FromResult(
                    new ResultadoEjecucionPowerShell(
                        true,
                        5,
                        string.Empty,
                        "No hay una aplicación asociada")));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "abre YouTube",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal(
            "error_al_abrir_web",
            resultado.Estado);
        Assert.Single(resultado.Pasos);
        Assert.Contains(
            "No hay una aplicación asociada",
            resultado.Mensaje,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Una_pagina_desconocida_se_busca_sin_abrir_esquemas_locales()
    {
        PeticionBasica resultado =
            ControlBasico.Interpretar(
                "abre la página file:///C:/secreto.txt");

        Assert.Equal(
            TipoPeticionBasica.BuscarEnInternet,
            resultado.Tipo);
        Assert.StartsWith(
            "https://www.google.com/search?q=",
            resultado.Objetivo,
            StringComparison.Ordinal);
    }

    private static DependenciasControlBasico CrearDependencias(
        Func<
            string,
            CancellationToken,
            Task<ResultadoEjecucionPowerShell>> ejecutar)
    {
        return new DependenciasControlBasico(
            _ => Task.FromResult<
                IReadOnlyList<AplicacionInstalada>>([]),
            ejecutar);
    }

    private static ResultadoEjecucionPowerShell Correcto()
    {
        return new ResultadoEjecucionPowerShell(
            true,
            0,
            string.Empty,
            string.Empty);
    }
}
