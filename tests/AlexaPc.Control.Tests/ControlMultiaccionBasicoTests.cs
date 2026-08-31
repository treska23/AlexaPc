using Xunit;

namespace ControlPCIA.Tests;

public sealed class ControlMultiaccionBasicoTests
{
    [Fact]
    public void Divide_solo_cuando_hay_otra_accion_clara()
    {
        Assert.Equal(
            [
                "abre la calculadora",
                "abre YouTube"
            ],
            ControlBasico.DividirAcciones(
                "abre la calculadora y luego abre YouTube"));
        Assert.Equal(
            [
                "pon la pantalla 3 como principal",
                "cambia la resolución del monitor 3 a 1920 por 1080"
            ],
            ControlBasico.DividirAcciones(
                "pon la pantalla 3 como principal y cambia la resolución del monitor 3 a 1920 por 1080"));
        Assert.Single(
            ControlBasico.DividirAcciones(
                "busca rock y metal en YouTube"));
    }

    [Fact]
    public async Task Traduce_todas_las_acciones_antes_de_ejecutar()
    {
        int ejecuciones = 0;
        DependenciasControlBasico dependencias =
            CrearDependencias(
                (_, _) =>
                {
                    ejecuciones++;
                    return Task.FromResult(Correcto());
                });

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "abre la calculadora y luego abre YouTube",
                true,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        Assert.Equal(2, resultado.Pasos.Count);
        Assert.Equal(0, ejecuciones);
        Assert.Contains(
            "Microsoft.WindowsCalculator",
            resultado.Pasos[0].Comando,
            StringComparison.Ordinal);
        Assert.Contains(
            "youtube.com",
            resultado.Pasos[1].Comando,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_ejecuta_nada_si_una_accion_no_se_puede_preparar()
    {
        int ejecuciones = 0;
        DependenciasControlBasico dependencias =
            CrearDependencias(
                (_, _) =>
                {
                    ejecuciones++;
                    return Task.FromResult(Correcto());
                });

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "abre la calculadora y luego crea una pista en Cubase",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal("no_disponible", resultado.Estado);
        Assert.Equal(0, ejecuciones);
        Assert.Empty(resultado.Pasos);
        Assert.Contains(
            "No he ejecutado ninguna",
            resultado.Mensaje,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ejecuta_en_orden_y_para_despues_del_primer_error()
    {
        var comandos = new List<string>();
        DependenciasControlBasico dependencias =
            CrearDependencias(
                (comando, _) =>
                {
                    comandos.Add(comando);
                    return Task.FromResult(
                        new ResultadoEjecucionPowerShell(
                            true,
                            4,
                            string.Empty,
                            "Windows rechazó la primera acción."));
                });

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "abre la calculadora y luego abre YouTube",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal("error_al_abrir", resultado.Estado);
        Assert.Single(comandos);
        Assert.Single(resultado.Pasos);
        Assert.Contains(
            "posteriores",
            resultado.Mensaje,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepara_dos_cambios_de_pantalla_en_orden()
    {
        DependenciasControlBasico dependencias =
            CrearDependencias(
                (_, _) =>
                    throw new InvalidOperationException(
                        "No debe ejecutar comandos."));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "pon la pantalla 3 como principal y cambia la resolución del monitor 3 a 1920 por 1080",
                true,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.Equal(2, resultado.Pasos.Count);
        Assert.Equal(
            "ControlPCIA.exe display primary 3",
            resultado.Pasos[0].Comando);
        Assert.Equal(
            "ControlPCIA.exe display resolution 3 1920 1080",
            resultado.Pasos[1].Comando);
    }

    private static DependenciasControlBasico CrearDependencias(
        Func<
            string,
            CancellationToken,
            Task<ResultadoEjecucionPowerShell>> ejecutar)
    {
        return new DependenciasControlBasico(
            _ => Task.FromResult<
                IReadOnlyList<AplicacionInstalada>>(
                [
                    new AplicacionInstalada(
                        "Calculator",
                        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")
                ]),
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
