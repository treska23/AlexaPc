using Xunit;

namespace ControlPCIA.Tests;

public sealed class EjecutorPowerShellTests
{
    [Fact]
    public async Task Ejecuta_una_consulta_permitida()
    {
        ResultadoEjecucionPowerShell resultado =
            await EjecutorPowerShell.EjecutarAsync(
                "Write-Output controlpcia-ok",
                TestContext.Current.CancellationToken);

        Assert.True(resultado.Ejecutado, resultado.Error);
        Assert.Equal(0, resultado.CodigoSalida);
        Assert.Equal("controlpcia-ok", resultado.Salida);
    }

    [Fact]
    public async Task No_inicia_PowerShell_para_un_comando_restringido()
    {
        ResultadoEjecucionPowerShell resultado =
            await EjecutorPowerShell.EjecutarAsync(
                "Remove-Item prueba.txt",
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Ejecutado);
        Assert.Equal(-1, resultado.CodigoSalida);
        Assert.StartsWith("BLOQUEADO:", resultado.Error);
    }

}
