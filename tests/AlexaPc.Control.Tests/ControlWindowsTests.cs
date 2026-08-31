using System.Text.Json;
using Xunit;

namespace ControlPCIA.Tests;

public sealed class ControlWindowsTests
{
    [Fact]
    public void El_contrato_es_general_y_deja_la_ejecucion_al_programa()
    {
        Assert.Contains(
            "Traduce lo que diga el usuario",
            ControlWindows.InstruccionSistema,
            StringComparison.Ordinal);
        Assert.Contains(
            "El programa,",
            ControlWindows.InstruccionSistema,
            StringComparison.Ordinal);
        Assert.Contains(
            "no tú, validará y ejecutará",
            ControlWindows.InstruccionSistema,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Cubase",
            ControlWindows.InstruccionSistema,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Get-StartApps",
            ControlWindows.InstruccionSistema,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            [
                "proponer_consulta",
                "proponer_comando",
                "preguntar_usuario"
            ],
            ControlWindows.Herramientas
                .Select(herramienta =>
                    herramienta.Funcion.Nombre)
                .ToArray());
    }

    [Fact]
    public async Task Modo_seguro_valida_la_propuesta_sin_ejecutarla()
    {
        int ejecuciones = 0;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    "Start-Process calc.exe",
                    "Get-Process -Name CalculatorApp | Select-Object ProcessName")
            ],
            (_, _) =>
            {
                ejecuciones++;
                throw new InvalidOperationException(
                    "No debe ejecutarse.");
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre la calculadora",
                null,
                null,
                TestContext.Current.CancellationToken,
                soloTraducir: true,
                dependencias);

        Assert.Equal(0, ejecuciones);
        Assert.False(resultado.Completado);
        Assert.Equal("prueba_sin_ejecucion", resultado.Estado);
        Assert.Equal(2, resultado.Pasos.Count);
        Assert.All(
            resultado.Pasos,
            paso => Assert.False(paso.Ejecutado));
    }

    [Fact]
    public async Task Modo_seguro_permite_consultas_y_no_ejecuta_la_accion()
    {
        var comandosEjecutados = new List<string>();
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerConsulta(
                    "Get-StartApps | Where-Object Name -Like '*Cubase*'"),
                ProponerComando(
                    "Start-Process 'C:\\Cubase\\Cubase.exe'",
                    "Get-Process -Name Cubase | Select-Object ProcessName")
            ],
            (comando, _) =>
            {
                comandosEjecutados.Add(comando);
                return Task.FromResult(
                    Correcto(
                        "Name=Cubase 14; AppID=C:\\Cubase\\Cubase.exe"));
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre Cubase",
                null,
                null,
                TestContext.Current.CancellationToken,
                soloTraducir: true,
                dependencias);

        Assert.Single(comandosEjecutados);
        Assert.Contains(
            "Get-StartApps",
            comandosEjecutados[0],
            StringComparison.Ordinal);
        Assert.Equal("prueba_sin_ejecucion", resultado.Estado);
        Assert.Equal(3, resultado.Pasos.Count);
        Assert.True(resultado.Pasos[0].Ejecutado);
        Assert.False(resultado.Pasos[1].Ejecutado);
        Assert.False(resultado.Pasos[2].Ejecutado);
    }

    [Fact]
    public async Task Entrega_al_modelo_las_aplicaciones_reales_relacionadas()
    {
        IReadOnlyList<MensajeOllama>? mensajesRecibidos = null;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    "Start-Process explorer.exe 'shell:AppsFolder\\MSEdge'",
                    "Get-Process -Name msedge | Select-Object ProcessName")
            ],
            (_, _) => Task.FromResult(Correcto()),
            observar: (mensajes, _) =>
                mensajesRecibidos = mensajes.ToArray(),
            contextoLocalAsync: (_, _) =>
                Task.FromResult(
                    """[{"nombre":"Microsoft Edge","appId":"MSEdge"}]"""));

        await ControlWindows.ControlarConDependenciasAsync(
            "abre Edge",
            null,
            null,
            TestContext.Current.CancellationToken,
            soloTraducir: true,
            dependencias);

        Assert.NotNull(mensajesRecibidos);
        MensajeOllama peticion = Assert.Single(
            mensajesRecibidos!,
            mensaje => mensaje.Rol == "user");
        Assert.Contains(
            "\"Microsoft Edge\"",
            peticion.Contenido,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"MSEdge\"",
            peticion.Contenido,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Una_consulta_la_ejecuta_ControlPCIA_y_el_modelo_redacta_la_respuesta()
    {
        int ejecuciones = 0;
        int aprendizajes = 0;
        IReadOnlyList<MensajeOllama>? segundaLlamada = null;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerConsulta(
                    "Get-Process | Where-Object MainWindowTitle | Select-Object ProcessName"),
                Texto("Tienes Microsoft Edge abierto.")
            ],
            (_, _) =>
            {
                ejecuciones++;
                return Task.FromResult(
                    Correcto("ProcessName=msedge"));
            },
            observar: (mensajes, herramientas) =>
            {
                Assert.Equal(3, herramientas.Count);

                if (mensajes.Any(mensaje =>
                        mensaje.Rol == "tool"))
                {
                    segundaLlamada = mensajes.ToArray();
                }
            },
            aprenderAsync: (_, _, _) =>
            {
                aprendizajes++;
                return Task.FromResult(true);
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "qué programas tengo abiertos",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal("respuesta", resultado.Estado);
        Assert.Equal(1, ejecuciones);
        Assert.Equal(0, aprendizajes);
        Assert.Equal(
            "Tienes Microsoft Edge abierto.",
            resultado.Mensaje);
        Assert.NotNull(segundaLlamada);
        MensajeOllama mensajeHerramienta =
            Assert.Single(
                segundaLlamada!,
                mensaje => mensaje.Rol == "tool");
        Assert.Equal(
            "proponer_consulta",
            mensajeHerramienta.NombreHerramienta);
        Assert.Contains(
            "ProcessName=msedge",
            mensajeHerramienta.Contenido,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Un_volcado_demasiado_amplio_obliga_a_refinar_la_consulta()
    {
        int ejecuciones = 0;
        IReadOnlyList<MensajeOllama>? mensajesRefinados = null;
        string volcado = string.Join(
            Environment.NewLine,
            Enumerable.Range(
                    1,
                    150)
                .Select(numero =>
                    $"Proceso{numero} Id={numero}"));
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerConsulta(
                    "Get-Process"),
                ProponerConsulta(
                    "Get-Process | Where-Object MainWindowTitle | Select-Object -First 20 ProcessName,MainWindowTitle"),
                Texto("Hay dos aplicaciones con ventana abierta.")
            ],
            (comando, _) =>
            {
                ejecuciones++;
                return Task.FromResult(
                    comando.Equals(
                        "Get-Process",
                        StringComparison.Ordinal)
                        ? Correcto(volcado)
                        : Correcto(
                            "msedge Edge\nnotepad Bloc de notas"));
            },
            observar: (mensajes, _) =>
            {
                if (mensajes.Any(mensaje =>
                        mensaje.Contenido.Contains(
                            "consulta_demasiado_amplia",
                            StringComparison.Ordinal)))
                {
                    mensajesRefinados = mensajes.ToArray();
                }
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "qué programas tengo abiertos",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal(2, ejecuciones);
        Assert.NotNull(mensajesRefinados);
        Assert.Contains(
            mensajesRefinados!,
            mensaje =>
                mensaje.Rol == "tool"
                && mensaje.Contenido.Contains(
                    "50 resultados",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task Una_accion_solo_se_completa_despues_de_la_verificacion()
    {
        var ejecutados = new List<string>();
        IReadOnlyList<string>? aprendidos = null;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    "Start-Process calc.exe",
                    "Get-Process -Name CalculatorApp | Select-Object Id,ProcessName")
            ],
            (comando, _) =>
            {
                ejecutados.Add(comando);
                return Task.FromResult(
                    comando.StartsWith(
                        "Start-Process",
                        StringComparison.Ordinal)
                        ? Correcto()
                        : Correcto(
                            "Id=7 ProcessName=CalculatorApp"));
            },
            aprenderAsync: (_, comandos, _) =>
            {
                aprendidos = comandos.ToArray();
                return Task.FromResult(true);
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre la calculadora",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal("completado", resultado.Estado);
        Assert.Equal(2, ejecutados.Count);
        Assert.Equal(ejecutados, aprendidos);
        Assert.Contains(
            "CalculatorApp",
            resultado.Mensaje,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Puede_consultar_el_PC_antes_de_proponer_la_accion()
    {
        var ejecutados = new List<string>();
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerConsulta(
                    "Get-Command -Name 'cubase*' -ErrorAction SilentlyContinue | Select-Object Source"),
                ProponerComando(
                    "Start-Process -FilePath 'C:\\Program Files\\Steinberg\\Cubase.exe'",
                    "Get-Process -Name Cubase | Select-Object Id,ProcessName")
            ],
            (comando, _) =>
            {
                ejecutados.Add(comando);

                if (comando.StartsWith(
                        "Get-Command",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Correcto(
                            "Source=C:\\Program Files\\Steinberg\\Cubase.exe"));
                }

                if (comando.StartsWith(
                        "Get-Process",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Correcto(
                            "Id=42 ProcessName=Cubase"));
                }

                return Task.FromResult(Correcto());
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre Cubase",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal(3, ejecutados.Count);
        Assert.StartsWith(
            "Get-Command",
            ejecutados[0],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Start-Process",
            ejecutados[1],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Get-Process",
            ejecutados[2],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task El_texto_libre_del_modelo_nunca_llega_al_ejecutor()
    {
        int ejecuciones = 0;
        DependenciasControlWindows dependencias = CrearDependencias(
            Enumerable.Repeat(
                Texto("Start-Process calc.exe"),
                8),
            (_, _) =>
            {
                ejecuciones++;
                return Task.FromResult(Correcto());
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre la calculadora",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.False(resultado.Completado);
        Assert.Equal("limite_pasos", resultado.Estado);
        Assert.Equal(0, ejecuciones);
        Assert.Empty(resultado.Pasos);
    }

    [Fact]
    public async Task Una_pregunta_estructurada_vuelve_al_movil_sin_ejecutarse()
    {
        int ejecuciones = 0;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                Preguntar(
                    "¿Qué nombre quieres ponerle al proyecto?")
            ],
            (_, _) =>
            {
                ejecuciones++;
                throw new InvalidOperationException();
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "crea un proyecto nuevo",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.False(resultado.Completado);
        Assert.Equal("requiere_aclaracion", resultado.Estado);
        Assert.Equal(0, ejecuciones);
        Assert.Contains(
            "nombre",
            resultado.Mensaje,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Una_accion_disfrazada_de_consulta_no_se_ejecuta()
    {
        var ejecutados = new List<string>();
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerConsulta(
                    "Start-Process calc.exe"),
                ProponerConsulta(
                    "Get-Process | Select-Object ProcessName"),
                Texto("Estos son los procesos abiertos.")
            ],
            (comando, _) =>
            {
                ejecutados.Add(comando);
                return Task.FromResult(
                    Correcto("ProcessName=msedge"));
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "qué procesos hay abiertos",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Single(ejecutados);
        Assert.StartsWith(
            "Get-Process",
            ejecutados[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Las_tres_prohibiciones_se_bloquean_antes_del_ejecutor()
    {
        int ejecuciones = 0;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    "Remove-Item -LiteralPath 'C:\\Datos\\nota.txt'",
                    "Test-Path -LiteralPath 'C:\\Datos\\nota.txt'")
            ],
            (_, _) =>
            {
                ejecuciones++;
                return Task.FromResult(Correcto());
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "elimina la nota",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.False(resultado.Completado);
        Assert.Equal("comando_rechazado", resultado.Estado);
        Assert.Equal(0, ejecuciones);
        Assert.Contains(
            "elimina",
            Assert.Single(resultado.Pasos).Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Una_orden_multitarea_llega_completa_en_una_propuesta()
    {
        const string script =
            "Start-Process notepad.exe; Start-Process calc.exe";
        const string verificacion =
            "Get-Process notepad,CalculatorApp | Select-Object ProcessName";
        var ejecutados = new List<string>();
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    script,
                    verificacion)
            ],
            (comando, _) =>
            {
                ejecutados.Add(comando);
                return Task.FromResult(
                    comando == verificacion
                        ? Correcto(
                            "notepad\nCalculatorApp")
                        : Correcto());
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre el bloc de notas y la calculadora",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal(
            [script, verificacion],
            ejecutados);
    }

    [Fact]
    public async Task Un_error_real_vuelve_al_modelo_y_admite_otra_propuesta()
    {
        int ejecuciones = 0;
        IReadOnlyList<MensajeOllama>? mensajesTrasError = null;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    "Start-Process AplicacionInventada.exe",
                    "Get-Process AplicacionInventada"),
                ProponerComando(
                    "Start-Process calc.exe",
                    "Get-Process CalculatorApp | Select-Object ProcessName")
            ],
            (comando, _) =>
            {
                ejecuciones++;

                if (comando.Contains(
                        "AplicacionInventada",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Error("No se encuentra el archivo."));
                }

                return Task.FromResult(
                    comando.StartsWith(
                        "Get-Process",
                        StringComparison.Ordinal)
                        ? Correcto(
                            "ProcessName=CalculatorApp")
                        : Correcto());
            },
            observar: (mensajes, _) =>
            {
                if (mensajes.Any(mensaje =>
                        mensaje.Rol == "tool"))
                {
                    mensajesTrasError = mensajes.ToArray();
                }
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre la calculadora",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal(3, ejecuciones);
        Assert.NotNull(mensajesTrasError);
        Assert.Contains(
            mensajesTrasError!,
            mensaje =>
                mensaje.Rol == "tool"
                && mensaje.Contenido.Contains(
                    "No se encuentra el archivo",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_da_exito_si_la_verificacion_no_aporta_evidencia()
    {
        int ejecuciones = 0;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    "Start-Process calc.exe",
                    "Get-Process CalculatorApp -ErrorAction SilentlyContinue"),
                ProponerComando(
                    "Start-Process calc.exe -PassThru | Out-Null",
                    "if (Get-Process CalculatorApp -ErrorAction SilentlyContinue) { 'CALCULADORA_ABIERTA' }")
            ],
            (comando, _) =>
            {
                ejecuciones++;

                return Task.FromResult(
                    comando.Contains(
                        "CALCULADORA_ABIERTA",
                        StringComparison.Ordinal)
                        ? Correcto("CALCULADORA_ABIERTA")
                        : Correcto());
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre la calculadora",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal(4, ejecuciones);
        Assert.Contains(
            "CALCULADORA_ABIERTA",
            resultado.Mensaje,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Una_peticion_aprendida_exacta_no_consulta_a_la_IA()
    {
        int llamadasIa = 0;
        var ejecutados = new List<string>();
        DependenciasControlWindows dependencias = CrearDependencias(
            [],
            (comando, _) =>
            {
                ejecutados.Add(comando);
                return Task.FromResult(
                    comando.StartsWith(
                        "Get-Process",
                        StringComparison.Ordinal)
                        ? Correcto("CALCULADORA_ABIERTA")
                        : Correcto());
            },
            recetas:
            [
                new RecetaReferencia(
                    "abre la calculadora",
                    [
                        "Start-Process calc.exe",
                        "Get-Process CalculatorApp | ForEach-Object { 'CALCULADORA_ABIERTA' }"
                    ],
                    3,
                    1)
            ],
            observar: (_, _) => llamadasIa++);

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "¡Abre la calculadora!",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal(0, llamadasIa);
        Assert.Equal(2, ejecutados.Count);
    }

    [Fact]
    public async Task Si_la_ruta_aprendida_falla_la_IA_recibe_el_error_real()
    {
        int llamadasIa = 0;
        int ejecuciones = 0;
        IReadOnlyList<MensajeOllama>? mensajesIa = null;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    "Start-Process calc.exe",
                    "Get-Process CalculatorApp | ForEach-Object { 'CALCULADORA_ABIERTA' }")
            ],
            (comando, _) =>
            {
                ejecuciones++;

                if (comando.Contains(
                        "AplicacionAntigua",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Error("La ruta aprendida ya no existe."));
                }

                return Task.FromResult(
                    comando.StartsWith(
                        "Get-Process",
                        StringComparison.Ordinal)
                        ? Correcto("CALCULADORA_ABIERTA")
                        : Correcto());
            },
            recetas:
            [
                new RecetaReferencia(
                    "abre la calculadora",
                    [
                        "Start-Process AplicacionAntigua.exe",
                        "Get-Process AplicacionAntigua | Select-Object ProcessName"
                    ],
                    4,
                    1)
            ],
            observar: (mensajes, _) =>
            {
                llamadasIa++;
                mensajesIa = mensajes.ToArray();
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre la calculadora",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal(1, llamadasIa);
        Assert.Equal(3, ejecuciones);
        Assert.NotNull(mensajesIa);
        Assert.Contains(
            mensajesIa!,
            mensaje =>
                mensaje.Contenido.Contains(
                    "La ruta aprendida ya no existe",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task Una_peticion_parecida_pero_no_identica_sigue_consultando_a_la_IA()
    {
        int llamadasIa = 0;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                Preguntar(
                    "¿Te refieres a la calculadora de Windows?")
            ],
            (_, _) =>
                throw new InvalidOperationException(),
            recetas:
            [
                new RecetaReferencia(
                    "abre la calculadora",
                    [
                        "Start-Process calc.exe",
                        "Get-Process CalculatorApp | Select-Object ProcessName"
                    ],
                    3,
                    0.85)
            ],
            observar: (_, _) => llamadasIa++);

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre mi calculadora científica",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.False(resultado.Completado);
        Assert.Equal("requiere_aclaracion", resultado.Estado);
        Assert.Equal(1, llamadasIa);
    }

    [Fact]
    public async Task Conserva_el_contexto_de_conversacion()
    {
        IReadOnlyList<MensajeOllama>? recibidos = null;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                Preguntar(
                    "¿En qué carpeta quieres crearlo?")
            ],
            (_, _) =>
                throw new InvalidOperationException(),
            observar: (mensajes, _) =>
                recibidos = mensajes.ToArray());

        await ControlWindows.ControlarConDependenciasAsync(
            "Proyecto Demo",
            null,
            [
                new MensajeConversacionControl(
                    "assistant",
                    "¿Qué nombre quieres ponerle al proyecto?")
            ],
            TestContext.Current.CancellationToken,
            false,
            dependencias);

        Assert.NotNull(recibidos);
        Assert.Contains(
            recibidos!,
            mensaje =>
                mensaje.Rol == "assistant"
                && mensaje.Contenido.Contains(
                    "¿Qué nombre",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_repite_la_misma_propuesta_que_no_avanza()
    {
        int ejecuciones = 0;
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerConsulta(
                    "Get-Command cubase -ErrorAction SilentlyContinue"),
                ProponerConsulta(
                    "Get-Command cubase -ErrorAction SilentlyContinue")
            ],
            (_, _) =>
            {
                ejecuciones++;
                return Task.FromResult(Correcto());
            });

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre Cubase",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.False(resultado.Completado);
        Assert.Equal("sin_progreso", resultado.Estado);
        Assert.Equal(1, ejecuciones);
    }

    [Fact]
    public async Task Puede_responder_una_conversacion_que_no_requiere_el_PC()
    {
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                Texto("Dos más cinco son siete.")
            ],
            (_, _) =>
                throw new InvalidOperationException());

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "cuánto es dos más cinco",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.Equal("conversacion", resultado.Estado);
        Assert.Equal(
            "Dos más cinco son siete.",
            resultado.Mensaje);
        Assert.Empty(resultado.Pasos);
    }

    [Fact]
    public async Task No_ejecuta_parcialmente_si_un_comando_no_existe()
    {
        var ejecutados = new List<string>();
        DependenciasControlWindows dependencias = CrearDependencias(
            [
                ProponerComando(
                    "Start-Process calc; Start-Program 'Notepad'",
                    "Get-Process calc,notepad"),
                ProponerComando(
                    "Start-Process calc; Start-Process notepad",
                    "Get-Process calc,notepad")
            ],
            (comando, _) =>
            {
                ejecutados.Add(comando);
                return Task.FromResult(
                    Correcto("calc notepad"));
            },
            comprobarAsync: (comando, _) =>
                Task.FromResult<IReadOnlyList<string>>(
                    comando.Contains(
                        "Start-Program",
                        StringComparison.Ordinal)
                        ? ["Start-Program"]
                        : []));

        ResultadoControl resultado =
            await ControlWindows.ControlarConDependenciasAsync(
                "abre la calculadora y el bloc de notas",
                null,
                null,
                TestContext.Current.CancellationToken,
                false,
                dependencias);

        Assert.True(resultado.Completado);
        Assert.DoesNotContain(
            ejecutados,
            comando => comando.Contains(
                "Start-Program",
                StringComparison.Ordinal));
        Assert.Equal(2, ejecutados.Count);
    }

    [Theory]
    [InlineData("Get-Process | Select-Object ProcessName")]
    [InlineData("Get-Command cubase")]
    [InlineData("Invoke-WebRequest 'https://learn.microsoft.com/' | Select-Object StatusCode")]
    [InlineData("winget search Cubase")]
    [InlineData("2 + 5")]
    public void Reconoce_consultas_generales(string comando)
    {
        Assert.True(
            ControlWindows.EsComandoDeConsulta(comando));
    }

    [Fact]
    public void Reconoce_el_listado_de_ventanas_como_consulta()
    {
        Assert.True(
            ControlWindows.EsComandoDeConsulta(
                "ControlPCIA.exe window --list --match 'Microsoft Edge'"));
        Assert.False(
            ControlWindows.EsComandoDeConsulta(
                "ControlPCIA.exe window --match 'Microsoft Edge' --foreground --state maximized"));
    }

    [Theory]
    [InlineData("Start-Process calc.exe")]
    [InlineData("Set-ItemProperty HKCU:\\Software\\X -Name Y -Value 1")]
    [InlineData("winget install Ejemplo")]
    [InlineData("Get-Process | ForEach-Object { $_.CloseMainWindow() }")]
    public void No_confunde_acciones_con_consultas(string comando)
    {
        Assert.False(
            ControlWindows.EsComandoDeConsulta(comando));
    }

    [Theory]
    [InlineData("abre Cubase")]
    [InlineData("pon un vídeo")]
    [InlineData("maximiza Edge")]
    [InlineData("crea un proyecto nuevo")]
    public void Detecta_peticiones_de_accion(string peticion)
    {
        Assert.True(
            ControlWindows.EsPeticionDeAccion(peticion));
    }

    [Theory]
    [InlineData("qué programas tengo abiertos")]
    [InlineData("dónde está el archivo informe")]
    [InlineData("cuánto es dos más cinco")]
    public void No_marca_consultas_como_acciones(string peticion)
    {
        Assert.False(
            ControlWindows.EsPeticionDeAccion(peticion));
    }

    private static MensajeOllama Texto(string contenido)
    {
        return new MensajeOllama(
            "assistant",
            contenido);
    }

    private static MensajeOllama ProponerConsulta(
        string comando)
    {
        return Herramienta(
            "proponer_consulta",
            ("comando", comando));
    }

    private static MensajeOllama ProponerComando(
        string comando,
        string verificacion)
    {
        return Herramienta(
            "proponer_comando",
            ("comando", comando),
            ("verificacion", verificacion));
    }

    private static MensajeOllama Preguntar(string pregunta)
    {
        return Herramienta(
            "preguntar_usuario",
            ("pregunta", pregunta));
    }

    private static MensajeOllama Herramienta(
        string nombre,
        params (string Nombre, string Valor)[] argumentos)
    {
        var valores = argumentos.ToDictionary(
            argumento => argumento.Nombre,
            argumento => JsonSerializer.SerializeToElement(
                argumento.Valor),
            StringComparer.Ordinal);

        return new MensajeOllama(
            "assistant",
            string.Empty,
            [
                new LlamadaHerramientaOllama(
                    "function",
                    new FuncionLlamadaOllama(
                        nombre,
                        valores))
            ]);
    }

    private static ResultadoEjecucionPowerShell Correcto(
        string salida = "")
    {
        return new ResultadoEjecucionPowerShell(
            true,
            0,
            salida,
            string.Empty);
    }

    private static ResultadoEjecucionPowerShell Error(
        string error)
    {
        return new ResultadoEjecucionPowerShell(
            true,
            1,
            string.Empty,
            error);
    }

    private static DependenciasControlWindows CrearDependencias(
        IEnumerable<MensajeOllama> respuestas,
        Func<
            string,
            CancellationToken,
            Task<ResultadoEjecucionPowerShell>> ejecutarAsync,
        IReadOnlyList<RecetaReferencia>? recetas = null,
        Action<
            IReadOnlyList<MensajeOllama>,
            IReadOnlyList<HerramientaOllama>>? observar = null,
        Func<
            string,
            IReadOnlyList<string>,
            CancellationToken,
            Task<bool>>? aprenderAsync = null,
        Func<
            string,
            CancellationToken,
            Task<string>>? contextoLocalAsync = null,
        Func<
            string,
            CancellationToken,
            Task<IReadOnlyList<string>>>? comprobarAsync = null)
    {
        var cola = new Queue<MensajeOllama>(respuestas);

        return new DependenciasControlWindows(
            (mensajes, herramientas, _) =>
            {
                observar?.Invoke(
                    mensajes,
                    herramientas);

                if (cola.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No quedan respuestas simuladas.");
                }

                return Task.FromResult(cola.Dequeue());
            },
            ejecutarAsync,
            (_, _) => Task.FromResult(
                recetas
                ?? (IReadOnlyList<RecetaReferencia>)[]),
            contextoLocalAsync
            ?? ((_, _) => Task.FromResult(string.Empty)),
            comprobarAsync
            ?? ((_, _) => Task.FromResult(
                (IReadOnlyList<string>)[])),
            aprenderAsync
            ?? ((_, _, _) => Task.FromResult(false)));
    }
}
