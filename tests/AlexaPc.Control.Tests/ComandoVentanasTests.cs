using Xunit;

namespace ControlPCIA.Tests;

public sealed class ComandoVentanasTests
{
    [Fact]
    public void Analiza_consulta_de_ventanas_sin_cambios()
    {
        bool correcto =
            ComandoVentanas.TryAnalizar(
                ["--list", "--match", "Microsoft Edge"],
                out OpcionesComandoVentana? opciones,
                out string error);

        Assert.True(correcto, error);
        Assert.NotNull(opciones);
        Assert.True(opciones.Listar);
        Assert.False(opciones.CambiaEstado);
        Assert.Equal("Microsoft Edge", opciones.Coincidencia);
    }

    [Fact]
    public void Analiza_estado_primer_plano_y_colocacion()
    {
        bool correcto =
            ComandoVentanas.TryAnalizar(
                [
                    "--match", "Cubase",
                    "--state", "maximized",
                    "--foreground",
                    "--x", "0",
                    "--y", "20",
                    "--width", "1920",
                    "--height", "1060"
                ],
                out OpcionesComandoVentana? opciones,
                out string error);

        Assert.True(correcto, error);
        Assert.NotNull(opciones);
        Assert.True(opciones.CambiaEstado);
        Assert.Equal(
            EstadoSolicitadoVentana.Maximizada,
            opciones.Estado);
        Assert.True(opciones.PrimerPlano);
        Assert.Equal(1920, opciones.Ancho);
    }

    [Fact]
    public void Rechaza_colocacion_incompleta()
    {
        bool correcto =
            ComandoVentanas.TryAnalizar(
                ["--match", "Edge", "--x", "0", "--y", "0"],
                out _,
                out string error);

        Assert.False(correcto);
        Assert.Contains("--width", error);
    }

    [Fact]
    public void Rechaza_accion_sin_ventana_objetivo()
    {
        bool correcto =
            ComandoVentanas.TryAnalizar(
                ["--foreground"],
                out _,
                out string error);

        Assert.False(correcto);
        Assert.Contains("--match", error);
    }

    [Fact]
    public void Rechaza_cierre_combinado_con_otros_cambios()
    {
        bool correcto =
            ComandoVentanas.TryAnalizar(
                [
                    "--match", "Visual Studio",
                    "--close",
                    "--foreground"
                ],
                out _,
                out string error);

        Assert.False(correcto);
        Assert.Contains("--close", error);
    }

    [Fact]
    public void Exige_estado_y_primer_plano_en_la_evidencia()
    {
        const string accion =
            "ControlPCIA.exe window --match 'Edge' --foreground --state maximized";
        const string verificacion =
            "ControlPCIA.exe window --list --match 'Edge'";
        const string evidencia =
            """
            {"correcto":true,"coincidencias":1,"ventanas":[{"estado":"maximized","primerPlano":true,"x":0,"y":0,"ancho":1920,"alto":1080}]}
            """;

        Assert.True(
            ControlWindows.VerificacionDemuestraResultado(
                accion,
                verificacion,
                evidencia));
        Assert.False(
            ControlWindows.VerificacionDemuestraResultado(
                accion,
                verificacion,
                evidencia.Replace(
                    "\"maximized\"",
                    "\"normal\"")));
        Assert.False(
            ControlWindows.VerificacionDemuestraResultado(
                accion,
                verificacion,
                evidencia.Replace(
                    "\"primerPlano\":true",
                    "\"primerPlano\":false")));
    }

    [Fact]
    public void No_acepta_un_json_sin_ventanas_como_exito()
    {
        Assert.False(
            ControlWindows.VerificacionDemuestraResultado(
                "Start-Process msedge.exe",
                "ControlPCIA.exe window --list --match 'Edge'",
                """
                {"correcto":true,"coincidencias":0,"ventanas":[]}
                """));
    }

    [Fact]
    public void Normaliza_la_verificacion_json_sin_otro_turno_de_ia()
    {
        Assert.Equal(
            "ControlPCIA.exe window --list --match 'Microsoft Edge'",
            ControlWindows.NormalizarVerificacionVentanas(
                "ControlPCIA.exe window --list --match 'Microsoft Edge' | Select-Object Title,State"));
    }
}
