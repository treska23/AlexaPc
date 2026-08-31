using Xunit;

namespace ControlPCIA.Tests;

public sealed class MemoriaTraduccionesTests
{
    [Fact]
    public async Task Aprende_y_recupera_una_traduccion_correcta()
    {
        string ruta = Path.Combine(
            Path.GetTempPath(),
            "ControlPCIA.Tests",
            Guid.NewGuid().ToString("N"),
            "traducciones.json");
        var memoria =
            new MemoriaTraducciones(ruta);

        bool aprendida =
            await memoria.AprenderAsync(
                "Consulta el nombre del PC",
                "hostname",
                true,
                TestContext.Current.CancellationToken);
        IReadOnlyList<TraduccionAprendida> resultados =
            await memoria.BuscarAsync(
                "consulta el nombre del pc",
                cancellationToken:
                    TestContext.Current.CancellationToken);

        Assert.True(aprendida);
        TraduccionAprendida resultado =
            Assert.Single(resultados);
        Assert.Equal(1, resultado.Similitud);
        Assert.Equal("hostname", resultado.Comando);
        Assert.True(resultado.Consulta);
        Assert.True(File.Exists(ruta));
    }

    [Fact]
    public async Task No_aprende_comandos_prohibidos()
    {
        string ruta = Path.Combine(
            Path.GetTempPath(),
            "ControlPCIA.Tests",
            Guid.NewGuid().ToString("N"),
            "traducciones.json");
        var memoria =
            new MemoriaTraducciones(ruta);

        bool aprendida =
            await memoria.AprenderAsync(
                "borra el archivo",
                "Remove-Item 'C:\\datos.txt'",
                false,
                TestContext.Current.CancellationToken);

        Assert.False(aprendida);
        Assert.False(File.Exists(ruta));
    }
}
