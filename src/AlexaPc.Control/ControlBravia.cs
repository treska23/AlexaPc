using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPCIA;

internal static class ControlBravia
{
    private const int SimpleIpPort = 20060;

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static readonly IReadOnlyDictionary<string, int> SimpleIpCodes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["KEYCODE_HOME"] = 6,
            ["KEYCODE_MENU"] = 7,
            ["KEYCODE_BACK"] = 8,
            ["KEYCODE_DPAD_UP"] = 9,
            ["KEYCODE_DPAD_DOWN"] = 10,
            ["KEYCODE_DPAD_RIGHT"] = 11,
            ["KEYCODE_DPAD_LEFT"] = 12,
            ["KEYCODE_DPAD_CENTER"] = 13,
            ["KEYCODE_VOLUME_UP"] = 30,
            ["KEYCODE_VOLUME_DOWN"] = 31,
            ["KEYCODE_VOLUME_MUTE"] = 32,
            ["KEYCODE_CHANNEL_UP"] = 33,
            ["KEYCODE_CHANNEL_DOWN"] = 34,
            ["KEYCODE_MEDIA_FAST_FORWARD"] = 77,
            ["KEYCODE_MEDIA_PLAY"] = 78,
            ["KEYCODE_MEDIA_PLAY_PAUSE"] = 78,
            ["KEYCODE_MEDIA_REWIND"] = 79,
            ["KEYCODE_MEDIA_STOP"] = 81,
            ["KEYCODE_MEDIA_PAUSE"] = 84,
            ["KEYCODE_TV_INPUT"] = 101,
            ["KEYCODE_SLEEP"] = 104,
            ["KEYCODE_TV_INPUT_HDMI_1"] = 124,
            ["KEYCODE_TV_INPUT_HDMI_2"] = 125,
            ["KEYCODE_TV_INPUT_HDMI_3"] = 126,
            ["KEYCODE_TV_INPUT_HDMI_4"] = 127
        };

    private static readonly IReadOnlyDictionary<string, string> IrccCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["KEYCODE_TV_INPUT"] = "AAAAAQAAAAEAAAAlAw==",
            ["KEYCODE_TV_INPUT_HDMI_1"] = "AAAAAgAAABoAAABaAw==",
            ["KEYCODE_TV_INPUT_HDMI_2"] = "AAAAAgAAABoAAABbAw==",
            ["KEYCODE_TV_INPUT_HDMI_3"] = "AAAAAgAAABoAAABcAw==",
            ["KEYCODE_TV_INPUT_HDMI_4"] = "AAAAAgAAABoAAABdAw==",
            ["KEYCODE_DPAD_UP"] = "AAAAAQAAAAEAAAB0Aw==",
            ["KEYCODE_DPAD_DOWN"] = "AAAAAQAAAAEAAAB1Aw==",
            ["KEYCODE_DPAD_RIGHT"] = "AAAAAQAAAAEAAAAzAw==",
            ["KEYCODE_DPAD_LEFT"] = "AAAAAQAAAAEAAAA0Aw==",
            ["KEYCODE_DPAD_CENTER"] = "AAAAAQAAAAEAAABlAw==",
            ["KEYCODE_HOME"] = "AAAAAQAAAAEAAABgAw==",
            ["KEYCODE_BACK"] = "AAAAAgAAAJcAAAAjAw==",
            ["KEYCODE_MENU"] = "AAAAAgAAAJcAAAA2Aw==",
            ["KEYCODE_VOLUME_UP"] = "AAAAAQAAAAEAAAASAw==",
            ["KEYCODE_VOLUME_DOWN"] = "AAAAAQAAAAEAAAATAw==",
            ["KEYCODE_VOLUME_MUTE"] = "AAAAAQAAAAEAAAAUAw==",
            ["KEYCODE_CHANNEL_UP"] = "AAAAAQAAAAEAAAAQAw==",
            ["KEYCODE_CHANNEL_DOWN"] = "AAAAAQAAAAEAAAARAw==",
            ["KEYCODE_MEDIA_PLAY"] = "AAAAAgAAAJcAAAAaAw==",
            ["KEYCODE_MEDIA_PAUSE"] = "AAAAAgAAAJcAAAAZAw==",
            ["KEYCODE_MEDIA_STOP"] = "AAAAAgAAAJcAAAAYAw=="
        };

    private static readonly IReadOnlyDictionary<string, string> AppPackages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["projectivy"] = "com.spocky.projengmenu",
            ["netflix"] = "com.netflix.ninja",
            ["youtube"] = "com.google.android.youtube.tv",
            ["prime video"] = "com.amazon.amazonvideo.livingroom",
            ["prime"] = "com.amazon.amazonvideo.livingroom",
            ["disney+"] = "com.disney.disneyplus",
            ["disney plus"] = "com.disney.disneyplus",
            ["disney"] = "com.disney.disneyplus",
            ["movistar+"] = "com.movistarplus.androidtv",
            ["movistar plus"] = "com.movistarplus.androidtv",
            ["movistar"] = "com.movistarplus.androidtv"
        };

    private static readonly Regex MencionaTelevision = new(
        @"\b(?:tele|television|televisión|tv|bravia|sony\s+bravia)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<ResultadoControl?> IntentarControlarAsync(
        string instruccion,
        CancellationToken cancellationToken = default,
        bool soloTraducir = false)
    {
        var texto = (instruccion ?? string.Empty).Trim();
        if (!MencionaTelevision.IsMatch(texto))
            return null;

        var orden = Interpretar(texto);
        if (orden is null)
        {
            return Error(
                "orden_tele_no_reconocida",
                "He entendido que la orden es para la Sony Bravia, pero todavía no reconozco esa acción.");
        }

        if (soloTraducir)
        {
            return new ResultadoControl(
                false,
                "prueba_sin_ejecucion",
                $"He preparado la orden de televisión: {orden.Descripcion}.",
                [new ResultadoPasoControl(1, orden.ComandoInterno, false, 0, string.Empty, string.Empty)],
                false);
        }

        try
        {
            var settings = CargarConfiguracion();
            string rutaUsada;

            if (orden.Tipo == TipoOrdenBravia.Encender)
            {
                rutaUsada = await EncenderAsync(settings, cancellationToken);
            }
            else if (orden.Tipo == TipoOrdenBravia.AbrirAplicacion)
            {
                rutaUsada = await AbrirAplicacionAsync(settings, orden.Valor!, cancellationToken);
            }
            else
            {
                rutaUsada = await EnviarTeclaAsync(settings, orden.Valor!, cancellationToken);
            }

            return new ResultadoControl(
                true,
                "completado",
                $"Tele: {orden.Descripcion} ({rutaUsada}).",
                [new ResultadoPasoControl(1, orden.ComandoInterno, true, 0, rutaUsada, string.Empty)],
                false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(
                "tele_no_disponible",
                $"No he podido controlar la Sony Bravia: {ex.Message}");
        }
    }

    private static OrdenBravia? Interpretar(string texto)
    {
        var normalizada = Normalizar(texto);

        foreach (var app in AppPackages)
        {
            if (normalizada.Contains(Normalizar(app.Key), StringComparison.Ordinal))
            {
                return new OrdenBravia(
                    TipoOrdenBravia.AbrirAplicacion,
                    app.Value,
                    $"abrir {app.Key}",
                    $"bravia app {app.Key}");
            }
        }

        var hdmi = Regex.Match(normalizada, @"\bhdmi\s*([1-4])\b", RegexOptions.CultureInvariant);
        if (hdmi.Success)
        {
            var numero = hdmi.Groups[1].Value;
            return Tecla($"KEYCODE_TV_INPUT_HDMI_{numero}", $"poner HDMI {numero}");
        }

        if (Regex.IsMatch(normalizada, @"\b(?:enciende|encender|despierta|despertar)\b", RegexOptions.CultureInvariant))
        {
            return new OrdenBravia(
                TipoOrdenBravia.Encender,
                null,
                "encender la televisión",
                "bravia wake");
        }

        if (Regex.IsMatch(normalizada, @"\b(?:apaga|apagar|reposo|duerme|dormir)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_SLEEP", "poner la televisión en reposo");

        if (Regex.IsMatch(normalizada, @"\b(?:sube|aumenta)\b.*\bvolumen\b|\bvolumen\b.*\b(?:sube|arriba|mas)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_VOLUME_UP", "subir el volumen");

        if (Regex.IsMatch(normalizada, @"\b(?:baja|reduce)\b.*\bvolumen\b|\bvolumen\b.*\b(?:baja|abajo|menos)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_VOLUME_DOWN", "bajar el volumen");

        if (Regex.IsMatch(normalizada, @"\b(?:silencio|mute|silencia|silenciar)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_VOLUME_MUTE", "alternar silencio");

        if (Regex.IsMatch(normalizada, @"\b(?:canal|channel)\b.*\b(?:sube|siguiente|arriba|mas)\b|\b(?:sube|siguiente)\b.*\bcanal\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_CHANNEL_UP", "subir de canal");

        if (Regex.IsMatch(normalizada, @"\b(?:canal|channel)\b.*\b(?:baja|anterior|abajo|menos)\b|\b(?:baja|anterior)\b.*\bcanal\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_CHANNEL_DOWN", "bajar de canal");

        if (Regex.IsMatch(normalizada, @"\b(?:pausa|pausar)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_MEDIA_PAUSE", "pausar");

        if (Regex.IsMatch(normalizada, @"\b(?:reanuda|reanudar|reproduce|reproducir|play)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_MEDIA_PLAY", "reanudar la reproducción");

        if (Regex.IsMatch(normalizada, @"\b(?:para|parar|deten|detener|stop)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_MEDIA_STOP", "detener la reproducción");

        if (Regex.IsMatch(normalizada, @"\b(?:adelanta|avanza|avance rapido)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_MEDIA_FAST_FORWARD", "avanzar rápidamente");

        if (Regex.IsMatch(normalizada, @"\b(?:rebobina|retrocede rapido|retroceso rapido)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_MEDIA_REWIND", "rebobinar");

        if (Regex.IsMatch(normalizada, @"\b(?:inicio|home)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_HOME", "ir a Inicio");

        if (Regex.IsMatch(normalizada, @"\b(?:atras|volver|vuelve)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_BACK", "volver atrás");

        if (Regex.IsMatch(normalizada, @"\b(?:menu|menú)\b.*\b(?:fuente|entrada|input)\b|\b(?:fuente|entrada|input)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_TV_INPUT", "abrir el menú de entradas");

        if (Regex.IsMatch(normalizada, @"\b(?:menu|menú)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_MENU", "abrir el menú");

        if (Regex.IsMatch(normalizada, @"\b(?:acepta|aceptar|ok|vale|selecciona|seleccionar)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_DPAD_CENTER", "pulsar OK");

        if (Regex.IsMatch(normalizada, @"\b(?:arriba|sube)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_DPAD_UP", "mover arriba");

        if (Regex.IsMatch(normalizada, @"\b(?:abajo|baja)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_DPAD_DOWN", "mover abajo");

        if (Regex.IsMatch(normalizada, @"\b(?:izquierda)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_DPAD_LEFT", "mover a la izquierda");

        if (Regex.IsMatch(normalizada, @"\b(?:derecha)\b", RegexOptions.CultureInvariant))
            return Tecla("KEYCODE_DPAD_RIGHT", "mover a la derecha");

        return null;
    }

    private static OrdenBravia Tecla(string keyCode, string descripcion) =>
        new(TipoOrdenBravia.Tecla, keyCode, descripcion, $"bravia key {keyCode}");

    private static async Task<string> EncenderAsync(
        BraviaSettingsData settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnviarAdbKeyAsync(settings, "KEYCODE_WAKEUP", cancellationToken);
            return "ADB wakeup";
        }
        catch when (!string.IsNullOrWhiteSpace(settings.MacAddress))
        {
            await EnviarWakeOnLanAsync(settings.MacAddress, cancellationToken);
            return "Wake-on-LAN";
        }
    }

    private static async Task<string> AbrirAplicacionAsync(
        BraviaSettingsData settings,
        string packageName,
        CancellationToken cancellationToken)
    {
        var adb = ResolverAdbPath(settings.AdbPath);
        var serial = $"{settings.IpAddress.Trim()}:{settings.Port}";
        await AsegurarAdbConectadoAsync(adb, serial, cancellationToken);

        var result = await EjecutarProcesoAsync(
            adb,
            ["-s", serial, "shell", "monkey", "-p", packageName, "-c", "android.intent.category.LAUNCHER", "1"],
            cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(TextoError(result));

        return "ADB";
    }

    private static async Task<string> EnviarTeclaAsync(
        BraviaSettingsData settings,
        string keyCode,
        CancellationToken cancellationToken)
    {
        if (await IntentarSimpleIpAsync(settings.IpAddress, keyCode, cancellationToken))
            return "Simple IP 20060";

        if (await IntentarIrccAsync(settings.IpAddress, settings.PreSharedKey, keyCode, cancellationToken))
            return "IRCC HTTP";

        await EnviarAdbKeyAsync(settings, keyCode, cancellationToken);
        return "ADB";
    }

    private static async Task<bool> IntentarSimpleIpAsync(
        string ipAddress,
        string keyCode,
        CancellationToken cancellationToken)
    {
        if (!SimpleIpCodes.TryGetValue(keyCode, out var irCode))
            return false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));

            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(ipAddress.Trim(), SimpleIpPort, timeout.Token);
            await using var stream = client.GetStream();
            var parameters = irCode.ToString("D16", CultureInfo.InvariantCulture);
            var frame = Encoding.ASCII.GetBytes($"*SCIRCC{parameters}\n");
            await stream.WriteAsync(frame, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IntentarIrccAsync(
        string ipAddress,
        string? preSharedKey,
        string keyCode,
        CancellationToken cancellationToken)
    {
        if (!IrccCodes.TryGetValue(keyCode, out var irccCode))
            return false;

        var body =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body><u:X_SendIRCC xmlns:u=\"urn:schemas-sony-com:service:IRCC:1\">" +
            $"<IRCCCode>{irccCode}</IRCCCode>" +
            "</u:X_SendIRCC></s:Body></s:Envelope>";

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://{ipAddress.Trim()}/sony/ircc");

            if (!string.IsNullOrWhiteSpace(preSharedKey))
                request.Headers.TryAddWithoutValidation("X-Auth-PSK", preSharedKey.Trim());

            request.Headers.TryAddWithoutValidation(
                "SOAPACTION",
                "\"urn:schemas-sony-com:service:IRCC:1#X_SendIRCC\"");
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType =
                MediaTypeHeaderValue.Parse("text/xml; charset=UTF-8");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task EnviarAdbKeyAsync(
        BraviaSettingsData settings,
        string keyCode,
        CancellationToken cancellationToken)
    {
        var adb = ResolverAdbPath(settings.AdbPath);
        var serial = $"{settings.IpAddress.Trim()}:{settings.Port}";
        await AsegurarAdbConectadoAsync(adb, serial, cancellationToken);

        var result = await EjecutarProcesoAsync(
            adb,
            ["-s", serial, "shell", "input", "keyevent", keyCode],
            cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(TextoError(result));
    }

    private static async Task AsegurarAdbConectadoAsync(
        string adb,
        string serial,
        CancellationToken cancellationToken)
    {
        var connect = await EjecutarProcesoAsync(adb, ["connect", serial], cancellationToken);
        if (connect.ExitCode != 0)
            throw new InvalidOperationException(TextoError(connect));
    }

    private static async Task EnviarWakeOnLanAsync(
        string macAddress,
        CancellationToken cancellationToken)
    {
        var hex = Regex.Replace(macAddress, "[^0-9A-Fa-f]", string.Empty);
        if (hex.Length != 12)
            throw new InvalidOperationException("La MAC configurada para la Bravia no es válida.");

        var mac = Enumerable.Range(0, 6)
            .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
            .ToArray();
        var packet = new byte[102];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 1; i <= 16; i++)
            Buffer.BlockCopy(mac, 0, packet, i * 6, 6);

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static BraviaSettingsData CargarConfiguracion()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonyBraviaControl",
            "settings.json");

        if (!File.Exists(path))
            return new BraviaSettingsData();

        try
        {
            return JsonSerializer.Deserialize<BraviaSettingsData>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new BraviaSettingsData();
        }
        catch
        {
            return new BraviaSettingsData();
        }
    }

    private static string ResolverAdbPath(string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            return preferredPath;

        const string executable = "adb.exe";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), executable);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                }
            }
        }

        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (Directory.Exists(downloads))
            {
                foreach (var directory in Directory.EnumerateDirectories(
                             downloads,
                             "platform-tools*",
                             SearchOption.TopDirectoryOnly))
                {
                    var direct = Path.Combine(directory, executable);
                    if (File.Exists(direct))
                        return direct;

                    var nested = Path.Combine(directory, "platform-tools", executable);
                    if (File.Exists(nested))
                        return nested;
                }
            }
        }
        catch
        {
        }

        return executable;
    }

    private static async Task<ProcessResult> EjecutarProcesoAsync(
        string executable,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"No se pudo iniciar '{executable}'.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"No se pudo ejecutar ADB en '{executable}'.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));

        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static string TextoError(ProcessResult result) =>
        string.IsNullOrWhiteSpace(result.ErrorText)
            ? result.OutputText.Trim()
            : result.ErrorText.Trim();

    private static string Normalizar(string text)
    {
        var formD = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        foreach (var character in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(800),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(1300)
        };
    }

    private static ResultadoControl Error(string estado, string mensaje) =>
        new(false, estado, mensaje, [], false);

    private sealed record OrdenBravia(
        TipoOrdenBravia Tipo,
        string? Valor,
        string Descripcion,
        string ComandoInterno);

    private sealed class BraviaSettingsData
    {
        public string IpAddress { get; set; } = "192.168.1.2";
        public int Port { get; set; } = 5555;
        public string MacAddress { get; set; } = string.Empty;
        public string AdbPath { get; set; } = string.Empty;
        public string PreSharedKey { get; set; } = string.Empty;
    }

    private sealed record ProcessResult(int ExitCode, string OutputText, string ErrorText);

    private enum TipoOrdenBravia
    {
        Tecla,
        Encender,
        AbrirAplicacion
    }
}
