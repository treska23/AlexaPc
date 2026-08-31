using Xunit;

namespace ControlPCIA.Tests;

public sealed class ComandoPantallasTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("modes", "2")]
    [InlineData("primary", "2")]
    [InlineData("resolution", "2", "1920", "1080")]
    [InlineData("resolution", "primary", "3840", "2160", "60")]
    [InlineData("frequency", "1", "144")]
    [InlineData("scale", "3", "150")]
    [InlineData("enable", "2")]
    [InlineData("disable", "2")]
    [InlineData("topology", "extend")]
    [InlineData("topology", "clone")]
    [InlineData("topology", "internal")]
    [InlineData("topology", "external")]
    [InlineData("orientation", "1", "portrait")]
    [InlineData("orientation", "2", "landscape-flipped")]
    [InlineData("position", "2", "-1920", "0")]
    [InlineData("place", "2", "right", "1")]
    public void Analiza_comandos_validos(
        params string[] argumentos)
    {
        bool correcto =
            ComandoPantallas.TryAnalizar(
                argumentos,
                out OpcionesComandoPantalla? opciones,
                out string error);

        Assert.True(correcto, error);
        Assert.NotNull(opciones);
    }

    [Theory]
    [InlineData()]
    [InlineData("resolution", "1", "1920")]
    [InlineData("frequency", "1", "ciento")]
    [InlineData("scale", "1", "133")]
    [InlineData("topology", "random")]
    [InlineData("orientation", "1", "diagonal")]
    [InlineData("position", "1", "0")]
    [InlineData("place", "1", "middle", "2")]
    public void Rechaza_comandos_incompletos_o_desconocidos(
        params string[] argumentos)
    {
        bool correcto =
            ComandoPantallas.TryAnalizar(
                argumentos,
                out _,
                out string error);

        Assert.False(correcto);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Conserva_la_frecuencia_opcional_de_la_resolucion()
    {
        bool correcto =
            ComandoPantallas.TryAnalizar(
                ["resolution", "2", "2560", "1440", "165"],
                out OpcionesComandoPantalla? opciones,
                out string error);

        Assert.True(correcto, error);
        Assert.NotNull(opciones);
        Assert.Equal(2560, opciones.Ancho);
        Assert.Equal(1440, opciones.Alto);
        Assert.Equal(165, opciones.Frecuencia);
    }
}
