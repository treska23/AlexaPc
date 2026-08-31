using AlexaPc.Agent.Services;
using Xunit;

namespace ControlPCIA.Tests;

public sealed class GeneralComputerControlServiceTests
{
    [Fact]
    public void No_lee_errores_de_powershell_ni_scripts_por_alexa()
    {
        var result = new GeneralControlResult(
            false,
            "error_configuracion_pantallas",
            "ControlPCIA.exe no se reconoce como comando de PowerShell. "
            + "At line: 1 | Start-Process ...",
            1);

        string message =
            GeneralComputerControlService.NormalizeSpokenMessage(
                result);

        Assert.Equal(
            "No he podido cambiar la configuración de las pantallas.",
            message);
    }

    [Fact]
    public void Conserva_un_error_breve_y_util_para_el_usuario()
    {
        var result = new GeneralControlResult(
            false,
            "error_configuracion_pantallas",
            "La pantalla 3 no existe.",
            1);

        string message =
            GeneralComputerControlService.NormalizeSpokenMessage(
                result);

        Assert.Equal(
            "La pantalla 3 no existe.",
            message);
    }
}
