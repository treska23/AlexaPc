using System.IO;
using Xunit;

namespace ControlPCIA.Tests;

public sealed class MemoriaRecetasTests
{
    [Fact]
    public async Task Guarda_y_recupera_una_receta_revalidada()
    {
        await ConMemoriaTemporalAsync(
            async memoria =>
            {
                bool aprendida = await memoria.AprenderAsync(
                    "Dime los procesos abiertos",
                    ["Get-Process | Select-Object ProcessName"],
                    TestContext.Current.CancellationToken);

                IReadOnlyList<RecetaReferencia> recetas =
                    await memoria.BuscarAsync(
                        "dime los procesos abiertos",
                        cancellationToken:
                            TestContext.Current.CancellationToken);

                Assert.True(aprendida);
                RecetaReferencia receta = Assert.Single(recetas);
                Assert.Equal("dime los procesos abiertos", receta.Intencion);
                Assert.Equal(
                    "Get-Process | Select-Object ProcessName",
                    Assert.Single(receta.Comandos));
                Assert.Equal(1, receta.Exitos);
            });
    }

    [Fact]
    public async Task No_guarda_comandos_bloqueados()
    {
        await ConMemoriaTemporalAsync(
            async memoria =>
            {
                bool aprendida = await memoria.AprenderAsync(
                    "borra una prueba",
                    ["Remove-Item C:\\prueba.txt"],
                    TestContext.Current.CancellationToken);

                Assert.False(aprendida);
                Assert.Equal(
                    0,
                    await memoria.ContarAsync(
                        TestContext.Current.CancellationToken));
            });
    }

    [Fact]
    public async Task Persiste_y_encuentra_intenciones_parecidas()
    {
        string carpeta = CrearCarpetaTemporal();
        string ruta = Path.Combine(carpeta, "recetas.json");

        try
        {
            var primeraInstancia = new MemoriaRecetas(ruta);

            Assert.True(
                await primeraInstancia.AprenderAsync(
                    "abre spotify",
                    [
                        "Get-StartApps | Where-Object Name -Like '*Spotify*'",
                        "explorer.exe 'shell:AppsFolder\\Spotify_123!App'",
                        "Get-Process -Name Spotify"
                    ],
                    TestContext.Current.CancellationToken));

            var segundaInstancia = new MemoriaRecetas(ruta);

            IReadOnlyList<RecetaReferencia> recetas =
                await segundaInstancia.BuscarAsync(
                    "inicia spotify",
                    cancellationToken:
                        TestContext.Current.CancellationToken);

            Assert.Single(recetas);
            Assert.True(recetas[0].Similitud >= 0.25);

            IReadOnlyList<RecetaReferencia> mismaAplicacionOtraAccion =
                await segundaInstancia.BuscarAsync(
                    "crea un proyecto nuevo en spotify",
                    cancellationToken:
                        TestContext.Current.CancellationToken);

            Assert.Single(mismaAplicacionOtraAccion);
            Assert.DoesNotContain(
                mismaAplicacionOtraAccion[0].Comandos,
                comando => comando.Contains(
                    "Get-StartApps",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public async Task Solo_guarda_una_apertura_si_incluye_comprobacion_por_consola()
    {
        await ConMemoriaTemporalAsync(
            async memoria =>
            {
                Assert.False(
                    await memoria.AprenderAsync(
                        "abre la aplicacion",
                        ["Start-Process aplicacion"],
                        TestContext.Current.CancellationToken));

                Assert.True(
                    await memoria.AprenderAsync(
                        "abre la aplicacion",
                        [
                            "Get-StartApps | Where-Object Name -Like '*Aplicacion*'",
                            "explorer.exe 'shell:AppsFolder\\Aplicacion_123!App'",
                            "Get-Process -Name Aplicacion"
                        ],
                        TestContext.Current.CancellationToken));

                RecetaReferencia receta = Assert.Single(
                    await memoria.BuscarAsync(
                        "abre la aplicacion",
                        cancellationToken:
                            TestContext.Current.CancellationToken));

                Assert.Equal(2, receta.Comandos.Count);
                Assert.DoesNotContain(
                    receta.Comandos,
                    comando => comando.Contains(
                        "Get-StartApps",
                        StringComparison.OrdinalIgnoreCase));
                Assert.Equal(
                    "Get-Process -Name Aplicacion",
                    receta.Comandos[1]);
            });
    }

    [Fact]
    public async Task No_aprende_recetas_del_modo_ui_legado()
    {
        await ConMemoriaTemporalAsync(
            async memoria =>
            {
                bool aprendida = await memoria.AprenderAsync(
                    "opera una aplicacion",
                    [
                        "ControlPCIA.exe ui inspect 'Aplicacion' 4",
                        "ControlPCIA.exe ui invoke 'Aplicacion' 'Aceptar' 'Button'"
                    ],
                    TestContext.Current.CancellationToken);

                Assert.False(aprendida);
                Assert.Equal(
                    0,
                    await memoria.ContarAsync(
                        TestContext.Current.CancellationToken));
            });
    }

    [Theory]
    [InlineData("  Pon MÚSICA, por favor.  ", "pon musica por favor")]
    [InlineData("Abre   Spotify", "abre spotify")]
    public void Normaliza_intenciones_sin_guardar_contenido_adicional(
        string original,
        string esperado)
    {
        Assert.Equal(esperado, MemoriaRecetas.Normalizar(original));
    }

    private static async Task ConMemoriaTemporalAsync(
        Func<MemoriaRecetas, Task> prueba)
    {
        string carpeta = CrearCarpetaTemporal();

        try
        {
            await prueba(
                new MemoriaRecetas(
                    Path.Combine(carpeta, "recetas.json")));
        }
        finally
        {
            Directory.Delete(carpeta, recursive: true);
        }
    }

    private static string CrearCarpetaTemporal()
    {
        string carpeta = Path.Combine(
            Path.GetTempPath(),
            "ControlPCIA.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(carpeta);
        return carpeta;
    }
}
