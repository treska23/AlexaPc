using Xunit;

namespace ControlPCIA.Tests;

public sealed class ControlBraviaTests
{
    [Theory]
    [InlineData("abre YouTube en la tele", "bravia app youtube")]
    [InlineData("sube el volumen de la televisión", "bravia key KEYCODE_VOLUME_UP")]
    [InlineData("pon HDMI 2 en la Bravia", "bravia key KEYCODE_TV_INPUT_HDMI_2")]
    [InlineData("pausa la tele", "bravia key KEYCODE_MEDIA_PAUSE")]
    [InlineData("apaga la tele", "bravia key KEYCODE_SLEEP")]
    public async Task Traduce_ordenes_de_la_bravia_sin_ejecutarlas(
        string texto,
        string comandoEsperado)
    {
        ResultadoControl? resultado =
            await ControlBravia.IntentarControlarAsync(
                texto,
                TestContext.Current.CancellationToken,
                soloTraducir: true);

        Assert.NotNull(resultado);
        Assert.Equal("prueba_sin_ejecucion", resultado.Estado);
        ResultadoPasoControl paso = Assert.Single(resultado.Pasos);
        Assert.False(paso.Ejecutado);
        Assert.Equal(comandoEsperado, paso.Comando);
    }

    [Fact]
    public async Task No_secuestra_una_orden_del_pc_que_no_menciona_la_tele()
    {
        ResultadoControl? resultado =
            await ControlBravia.IntentarControlarAsync(
                "abre YouTube",
                TestContext.Current.CancellationToken,
                soloTraducir: true);

        Assert.Null(resultado);
    }
}
