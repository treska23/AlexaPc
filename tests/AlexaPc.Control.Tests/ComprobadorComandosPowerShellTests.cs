using Xunit;

namespace ControlPCIA.Tests;

public sealed class ComprobadorComandosPowerShellTests
{
    [Fact]
    public void Extrae_comandos_y_excluye_funciones_declaradas()
    {
        string[] nombres =
            ComprobadorComandosPowerShell.ObtenerNombresEstaticos(
                """
                function Abrir-Aplicacion { param($ruta) Start-Process $ruta }
                Abrir-Aplicacion 'calc.exe'
                Start-Program 'Notepad'
                """);

        Assert.Contains("Start-Process", nombres);
        Assert.Contains("Start-Program", nombres);
        Assert.DoesNotContain("Abrir-Aplicacion", nombres);
    }

    [Fact]
    public async Task Consulta_a_powershell_y_detecta_un_nombre_inventado()
    {
        ComprobadorComandosPowerShell.InvalidarCache();

        IReadOnlyList<string> ausentes =
            await ComprobadorComandosPowerShell
                .ObtenerNoDisponiblesAsync(
                    "Start-Process calc; ComandoImposibleControlPciaXYZ",
                    TestContext.Current.CancellationToken);

        Assert.Contains(
            "ComandoImposibleControlPciaXYZ",
            ausentes);
        Assert.DoesNotContain(
            "Start-Process",
            ausentes);
    }
}
