using Xunit;

namespace ControlPCIA.Tests;

public sealed class ControlMultimediaBasicoTests
{
    public static TheoryData<string, string> Ordenes => new()
    {
        {
            "pausa la reproducción",
            "pause"
        },
        {
            "para el vídeo que estoy viendo por internet",
            "pause --app 'browser'"
        },
        {
            "haz play en Spotify",
            "play --app 'spotify'"
        },
        {
            "reanuda el vídeo de YouTube",
            "play --app 'browser'"
        },
        {
            "pon la siguiente canción en Spotify",
            "next --app 'spotify'"
        },
        {
            "vuelve a la canción anterior",
            "previous"
        },
        {
            "adelanta el vídeo de internet 30 segundos",
            "seek 30 --app 'browser'"
        },
        {
            "retrocede la reproducción 12,5 segundos",
            "seek -12.5"
        },
        {
            "activa el modo aleatorio en Spotify",
            "shuffle on --app 'spotify'"
        },
        {
            "repite esta canción",
            "repeat track"
        },
        {
            "qué canción se está reproduciendo en Spotify",
            "status --app 'spotify'"
        },
        {
            "pon el vídeo de internet en pantalla completa",
            "fullscreen --app 'browser'"
        },
        {
            "sal de la pantalla completa del vídeo de YouTube",
            "exit-fullscreen --app 'browser'"
        }
    };

    [Theory]
    [MemberData(nameof(Ordenes))]
    public void Traduce_ordenes_multimedia(
        string texto,
        string argumentos)
    {
        PeticionBasica resultado =
            ControlBasico.Interpretar(texto);

        Assert.Equal(
            TipoPeticionBasica.ControlarMultimedia,
            resultado.Tipo);
        Assert.Equal(argumentos, resultado.Objetivo);
    }

    [Fact]
    public async Task Modo_de_prueba_prepara_un_comando_y_no_controla_nada()
    {
        DependenciasControlBasico dependencias = new(
            _ => Task.FromResult<
                IReadOnlyList<AplicacionInstalada>>([]),
            (_, _) =>
                throw new InvalidOperationException(
                    "No debe ejecutar PowerShell."));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "pausa Spotify",
                true,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        ResultadoPasoControl paso =
            Assert.Single(resultado.Pasos);
        Assert.Equal(
            "ControlPCIA.exe media pause --app 'spotify'",
            paso.Comando);
        Assert.True(
            ValidadorPowerShell.Validar(
                    paso.Comando)
                .Permitido);
    }

    [Fact]
    public async Task Entrega_el_rechazo_de_la_aplicacion()
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
                    {"correcto":false,"detalle":"Spotify no admite detener esta sesión."}
                    """)));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "detén la reproducción en Spotify",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal(
            "error_control_multimedia",
            resultado.Estado);
        Assert.Equal(
            "Spotify no admite detener esta sesión.",
            resultado.Mensaje);
    }
}
