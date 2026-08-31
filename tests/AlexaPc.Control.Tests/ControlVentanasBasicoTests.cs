using Xunit;

namespace ControlPCIA.Tests;

public sealed class ControlVentanasBasicoTests
{
    public static TheoryData<string, string> Ordenes => new()
    {
        {
            "trae la calculadora al frente",
            "ControlPCIA.exe window --match 'calculadora' --foreground"
        },
        {
            "pon Microsoft Edge en primer plano",
            "ControlPCIA.exe window --match 'microsoft edge' --foreground"
        },
        {
            "maximiza Visual Studio",
            "ControlPCIA.exe window --match 'visual studio' --state maximized --foreground"
        },
        {
            "minimiza Cubase",
            "ControlPCIA.exe window --match 'cubase' --state minimized"
        },
        {
            "restaura la calculadora",
            "ControlPCIA.exe window --match 'calculadora' --state normal --foreground"
        },
        {
            "cierra Visual Studio",
            "ControlPCIA.exe window --match 'visual studio' --close"
        },
        {
            "coloca Edge en x 0 y 20 ancho 1920 alto 1060",
            "ControlPCIA.exe window --match 'edge' --x 0 --y 20 --width 1920 --height 1060 --foreground"
        }
    };

    [Theory]
    [MemberData(nameof(Ordenes))]
    public async Task Traduce_ordenes_de_ventana(
        string texto,
        string comando)
    {
        DependenciasControlBasico dependencias = new(
            _ => Task.FromResult<
                IReadOnlyList<AplicacionInstalada>>([]),
            (_, _) =>
                throw new InvalidOperationException(
                    "No debe ejecutar PowerShell."));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                texto,
                true,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        ResultadoPasoControl paso =
            Assert.Single(resultado.Pasos);
        Assert.Equal(comando, paso.Comando);
        Assert.False(paso.Ejecutado);
        Assert.True(
            ValidadorPowerShell.Validar(
                    paso.Comando)
                .Permitido);
    }

    [Fact]
    public async Task Devuelve_al_movil_el_aviso_de_trabajo_sin_guardar()
    {
        DependenciasControlBasico dependencias = new(
            _ => Task.FromResult<
                IReadOnlyList<AplicacionInstalada>>([]),
            (_, _) => Task.FromResult(
                new ResultadoEjecucionPowerShell(
                    true,
                    4,
                    string.Empty,
                    """
                    {"correcto":false,"detalle":"La ventana sigue abierta. La aplicación puede estar esperando una decisión, por ejemplo guardar trabajo."}
                    """)));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "cierra Visual Studio",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal(
            "error_control_ventana",
            resultado.Estado);
        Assert.Contains(
            "guardar trabajo",
            resultado.Mensaje,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_intercepta_una_apertura_de_aplicacion()
    {
        Assert.Null(
            ControlVentanasBasico.Interpretar(
                "abre la calculadora"));
    }
}
