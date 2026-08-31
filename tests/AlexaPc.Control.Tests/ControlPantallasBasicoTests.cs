using Xunit;

namespace ControlPCIA.Tests;

public sealed class ControlPantallasBasicoTests
{
    public static TheoryData<string, string> Ordenes => new()
    {
        {
            "qué pantallas tengo conectadas",
            "list"
        },
        {
            "dime qué resoluciones soporta el monitor 2",
            "modes 2"
        },
        {
            "pon la pantalla 2 como principal",
            "primary 2"
        },
        {
            "quiero que la pantalla número tres sea la principal",
            "primary 3"
        },
        {
            "usa el monitor dos como principal",
            "primary 2"
        },
        {
            "cambia la resolución del monitor 2 a 1920 por 1080",
            "resolution 2 1920 1080"
        },
        {
            "pon la pantalla principal en 4K a 60 Hz",
            "resolution primary 3840 2160 60"
        },
        {
            "configura el monitor 1 a 144 hercios",
            "frequency 1 144"
        },
        {
            "pon la escala del monitor 3 al 150 por ciento",
            "scale 3 150"
        },
        {
            "cambia el zoom de la pantalla número uno al 125%",
            "scale 1 125"
        },
        {
            "desactiva la pantalla 3",
            "disable 3"
        },
        {
            "vuelve a activar el monitor 2",
            "enable 2"
        },
        {
            "reactiva la pantalla cuatro",
            "enable 4"
        },
        {
            "duplica las pantallas",
            "topology clone"
        },
        {
            "extiende el escritorio entre los monitores",
            "topology extend"
        },
        {
            "usa solo la pantalla del PC",
            "topology internal"
        },
        {
            "pon solo la segunda pantalla",
            "topology external"
        },
        {
            "usa únicamente el proyector",
            "topology external"
        },
        {
            "gira el monitor 2 en vertical",
            "orientation 2 portrait"
        },
        {
            "pon la pantalla 1 horizontal invertida",
            "orientation 1 landscape-flipped"
        },
        {
            "gira el monitor número tres 270 grados",
            "orientation 3 portrait-flipped"
        },
        {
            "coloca la pantalla 2 en x -1920 y 0",
            "position 2 -1920 0"
        },
        {
            "coloca la pantalla 2 a la derecha de la pantalla 1",
            "place 2 right 1"
        }
    };

    [Theory]
    [MemberData(nameof(Ordenes))]
    public void Traduce_ordenes_naturales_a_un_comando_de_pantalla(
        string texto,
        string argumentos)
    {
        PeticionBasica resultado =
            ControlBasico.Interpretar(texto);

        Assert.Equal(
            TipoPeticionBasica.GestionarPantallas,
            resultado.Tipo);
        Assert.Equal(argumentos, resultado.Objetivo);
    }

    [Fact]
    public async Task Modo_de_prueba_prepara_un_solo_comando_y_no_lo_ejecuta()
    {
        DependenciasControlBasico dependencias = new(
            _ => Task.FromResult<
                IReadOnlyList<AplicacionInstalada>>([]),
            (_, _) =>
                throw new InvalidOperationException(
                    "No debe ejecutar PowerShell."));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "pon la pantalla 2 como principal",
                true,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            "prueba_sin_ejecucion",
            resultado.Estado);
        ResultadoPasoControl paso =
            Assert.Single(resultado.Pasos);
        Assert.Equal(
            "ControlPCIA.exe display primary 2",
            paso.Comando);
        Assert.False(paso.Ejecutado);
        Assert.True(
            ValidadorPowerShell.Validar(
                    paso.Comando)
                .Permitido);
    }

    [Fact]
    public async Task Entrega_el_error_de_windows_al_movil()
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
                    {"correcto":false,"detalle":"La pantalla 3 no existe."}
                    """)));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "desactiva la pantalla 3",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.False(resultado.Completado);
        Assert.Equal(
            "error_configuracion_pantallas",
            resultado.Estado);
        Assert.Equal(
            "La pantalla 3 no existe.",
            resultado.Mensaje);
    }

    [Fact]
    public async Task Formatea_la_consulta_de_pantallas_para_el_movil()
    {
        const string json =
            """
            {
              "correcto": true,
              "detalle": "Consulta completada.",
              "pantallas": [
                {
                  "Numero": 1,
                  "Dispositivo": "\\\\.\\DISPLAY1",
                  "Adaptador": "NVIDIA",
                  "Monitor": "LG TV",
                  "Activa": true,
                  "Principal": true,
                  "X": 0,
                  "Y": 0,
                  "Ancho": 3840,
                  "Alto": 2160,
                  "Frecuencia": 60,
                  "Orientacion": 0
                }
              ]
            }
            """;
        DependenciasControlBasico dependencias = new(
            _ => Task.FromResult<
                IReadOnlyList<AplicacionInstalada>>([]),
            (_, _) => Task.FromResult(
                new ResultadoEjecucionPowerShell(
                    true,
                    0,
                    json,
                    string.Empty)));

        ResultadoControl resultado =
            await ControlBasico.ControlarConDependenciasAsync(
                "qué pantallas tengo conectadas",
                false,
                dependencias,
                TestContext.Current.CancellationToken);

        Assert.True(resultado.Completado);
        Assert.Equal("respuesta", resultado.Estado);
        Assert.Contains("LG TV", resultado.Mensaje);
        Assert.Contains("3840x2160", resultado.Mensaje);
        Assert.Contains("principal", resultado.Mensaje);
    }
}
