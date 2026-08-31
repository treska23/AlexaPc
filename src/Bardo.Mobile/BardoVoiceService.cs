using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Net.Wifi;
using Android.OS;
using Android.Util;
using Bardo.Mobile.Infrastructure;
using System.Text.RegularExpressions;

namespace Bardo.Mobile;

[Service(
    Name = "com.treska23.bardo.BardoVoiceService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone)]
public sealed class BardoVoiceService : Service
{
    private const string ChannelId = "bardo-listening";
    private const int NotificationId = 1701;
    private const string ActionStop = "com.treska23.bardo.STOP";
    private const string LogTag = "BardoVoice";

    private readonly RelayCommandClient _relayClient = new();
    private readonly WakeOnLanClient _wakeOnLanClient = new();
    private readonly CancellationTokenSource _shutdown = new();

    private PowerManager.WakeLock? _cpuWakeLock;
    private WifiManager.WifiLock? _wifiLock;
    private ToneGenerator? _feedbackTone;
    private AndroidSpeechEngine? _speechEngine;
    private BardoSettings _settings = BardoSettings.Default;
    private bool _destroyed;
    private bool _commandInFlight;

    public static bool IsRunning { get; private set; }
    public static bool LocalEngineReady { get; private set; }
    public static string CurrentStatus { get; private set; } = "detenido";
    public static float LastRmsDb { get; private set; } = float.NaN;
    public static string LastRecognizerEvent { get; private set; } = "sin eventos";
    public static event Action<string>? StatusChanged;

    public override void OnCreate()
    {
        base.OnCreate();
        IsRunning = true;
        LocalEngineReady = false;
        CurrentStatus = "iniciando servicio";
        LastRmsDb = float.NaN;
        LastRecognizerEvent = "iniciando motor local";

        try
        {
            _settings = BardoSettingsStore.Load(this);
            LegacyLocalSpeechModelCleaner.Clean(this);
            CreateNotificationChannel();
            StartAsForeground("Preparando voz local de Android…");
            AcquireDedicatedResourceLocks();

            LocalEngineReady = AndroidSpeechEngine.IsAvailable(this);
            if (!LocalEngineReady)
            {
                SetServiceStatus("El reconocimiento local de Android no está disponible");
                return;
            }

            _speechEngine = new AndroidSpeechEngine(
                this,
                settingsProvider: () => BardoSettingsStore.Load(this),
                executeCommand: ExecuteCommandAndResumeAsync,
                playWakeAcknowledgement: PlayWakeAcknowledgement,
                setStatus: SetServiceStatus,
                setEvent: message => LastRecognizerEvent = message,
                setRms: rms => LastRmsDb = rms);
            _speechEngine.Start();
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, $"OnCreate falló: {ex}");
            LastRecognizerEvent = $"fallo: {ex.GetType().Name}";
            SetServiceStatus($"Fallo al iniciar: {ex.GetType().Name}: {ex.Message}");
            StopSelf();
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        _settings = BardoSettingsStore.Load(this);
        _speechEngine?.Start();

        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        _destroyed = true;
        IsRunning = false;
        LocalEngineReady = false;
        CurrentStatus = "detenido";
        LastRecognizerEvent = "servicio detenido";
        StatusChanged?.Invoke(CurrentStatus);

        _shutdown.Cancel();
        try
        {
            _speechEngine?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Error cerrando voz de Android: {ex.Message}");
        }
        _speechEngine = null;

        ReleaseFeedbackTone();
        ReleaseDedicatedResourceLocks();
        _shutdown.Dispose();
        base.OnDestroy();
    }

    private async Task ExecuteCommandAndResumeAsync(
        string command,
        CancellationToken cancellationToken)
    {
        if (_commandInFlight || _destroyed)
        {
            return;
        }

        string normalizedCommand = NormalizeCommand(command);
        if (normalizedCommand.Length == 0)
        {
            return;
        }

        _commandInFlight = true;
        try
        {
            if (IsWakeComputerCommand(normalizedCommand))
            {
                await WakeComputerAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (IsShutdownComputerCommand(normalizedCommand))
            {
                bool macReady = await EnsurePcMacKnownAsync(cancellationToken).ConfigureAwait(false);
                if (!macReady)
                {
                    SetServiceStatus("No apago el PC hasta guardar su MAC para poder volver a encenderlo");
                    await Task.Delay(1_200, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // El agente ya tiene esta acción integrada y programa un apagado limpio
                // con cinco segundos de margen para devolver la respuesta por el relay.
                normalizedCommand = "apaga ordenador";
            }

            SetServiceStatus($"Ejecutando: {normalizedCommand}");
            LastRecognizerEvent = $"SEND · {normalizedCommand}";
            Log.Info(LogTag, $"SEND local once: {normalizedCommand}");

            RelayCommandResult result = await _relayClient.SendAsync(
                _settings,
                normalizedCommand,
                cancellationToken).ConfigureAwait(false);

            if (_destroyed)
            {
                return;
            }

            Log.Info(LogTag, $"Relay success={result.Success}: {result.Message}");
            SetServiceStatus(result.Success
                ? $"Hecho · {result.Message}"
                : $"Error · {result.Message}");
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandInFlight = false;
        }
    }

    private async Task WakeComputerAsync(CancellationToken cancellationToken)
    {
        bool macReady = await EnsurePcMacKnownAsync(cancellationToken).ConfigureAwait(false);
        if (!macReady)
        {
            SetServiceStatus("No conozco la MAC del PC · enciéndelo una vez manualmente para que Bardo pueda aprenderla");
            await Task.Delay(1_200, cancellationToken).ConfigureAwait(false);
            return;
        }

        string mac = WakeOnLanClient.NormalizeMac(_settings.PcMacAddress);
        SetServiceStatus("Encendiendo ordenador…");
        LastRecognizerEvent = $"WOL · {mac}";
        Log.Info(LogTag, $"Enviando Wake-on-LAN a {mac}");
        await _wakeOnLanClient.WakeAsync(mac, cancellationToken).ConfigureAwait(false);
        SetServiceStatus("Hecho · señal de encendido enviada al ordenador");
        await Task.Delay(700, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> EnsurePcMacKnownAsync(CancellationToken cancellationToken)
    {
        if (WakeOnLanClient.IsValidMac(_settings.PcMacAddress))
        {
            string normalized = WakeOnLanClient.NormalizeMac(_settings.PcMacAddress);
            if (!string.Equals(normalized, _settings.PcMacAddress, StringComparison.Ordinal))
            {
                _settings = _settings with { PcMacAddress = normalized };
                BardoSettingsStore.Save(this, _settings);
            }

            return true;
        }

        SetServiceStatus("Aprendiendo la MAC del ordenador…");
        LastRecognizerEvent = "WOL · solicitando MAC al agente";

        RelayCommandResult result = await _relayClient.SendAsync(
            _settings,
            "mac ordenador",
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            Log.Warn(LogTag, $"No se pudo aprender la MAC: {result.Message}");
            return false;
        }

        Match match = Regex.Match(
            result.Message ?? string.Empty,
            @"(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}",
            RegexOptions.CultureInvariant);

        if (!match.Success || !WakeOnLanClient.IsValidMac(match.Value))
        {
            Log.Warn(LogTag, $"La respuesta del agente no contenía una MAC válida: {result.Message}");
            return false;
        }

        string mac = WakeOnLanClient.NormalizeMac(match.Value);
        _settings = _settings with { PcMacAddress = mac };
        BardoSettingsStore.Save(this, _settings);
        LastRecognizerEvent = $"WOL · MAC aprendida {mac}";
        SetServiceStatus($"Wake-on-LAN preparado · MAC {mac}");
        Log.Info(LogTag, $"MAC del PC aprendida y guardada: {mac}");
        return true;
    }

    private static bool IsWakeComputerCommand(string command) =>
        Regex.IsMatch(
            command,
            @"\b(?:enciende|encender|despierta|despertar|arranca|arrancar)\b.*\b(?:ordenador|computadora|pc|equipo)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsShutdownComputerCommand(string command) =>
        Regex.IsMatch(
            command,
            @"\b(?:apaga|apagar)\b.*\b(?:ordenador|computadora|pc|equipo)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private string NormalizeCommand(string command)
    {
        string normalized = command.Trim();
        if (normalized.Length == 0)
        {
            return normalized;
        }

        string withoutWakeWord = WakeWordMatcher.ExtractCommandAfterWakeWord(
            normalized,
            _settings.WakeWord);
        if (withoutWakeWord.Length > 0)
        {
            normalized = withoutWakeWord;
        }
        else if (WakeWordMatcher.Matches(normalized, _settings.WakeWord) &&
                 Regex.Matches(normalized, @"\p{L}+", RegexOptions.CultureInvariant).Count <= 2)
        {
            // Si durante la escucha de comando sólo repite «Bardo», no mandamos eso
            // al PC como si fuera una orden.
            return string.Empty;
        }

        return normalized.Trim(' ', ',', '.', ';', ':', '¿', '?', '¡', '!');
    }

    private void PlayWakeAcknowledgement()
    {
        try
        {
            _feedbackTone ??= new ToneGenerator(Android.Media.Stream.Alarm, 85);
            _feedbackTone.StopTone();
            _feedbackTone.StartTone(Tone.PropAck, 180);
            Log.Info(LogTag, "Tono de wake word reproducido");
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"No se pudo reproducir el tono de wake word: {ex}");
        }
    }

    private void ReleaseFeedbackTone()
    {
        try
        {
            _feedbackTone?.StopTone();
            _feedbackTone?.Release();
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"No se pudo liberar el tono: {ex}");
        }
        finally
        {
            _feedbackTone?.Dispose();
            _feedbackTone = null;
        }
    }

    private void AcquireDedicatedResourceLocks()
    {
        try
        {
            var powerManager = (PowerManager?)GetSystemService(PowerService);
            _cpuWakeLock = powerManager?.NewWakeLock(
                WakeLockFlags.Partial,
                "com.treska23.bardo:voice-cpu");
            _cpuWakeLock?.SetReferenceCounted(false);
            _cpuWakeLock?.Acquire();

            var wifiManager = (WifiManager?)ApplicationContext?.GetSystemService(WifiService);
            _wifiLock = wifiManager?.CreateWifiLock(
                WifiMode.FullHighPerf,
                "com.treska23.bardo:voice-wifi");
            _wifiLock?.SetReferenceCounted(false);
            _wifiLock?.Acquire();

            Log.Info(
                LogTag,
                $"Recursos dedicados: CPU={_cpuWakeLock?.IsHeld == true}, WiFi={_wifiLock?.IsHeld == true}");
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"No se pudieron reservar todos los recursos dedicados: {ex}");
        }
    }

    private void ReleaseDedicatedResourceLocks()
    {
        try
        {
            if (_wifiLock?.IsHeld == true)
            {
                _wifiLock.Release();
            }

            if (_cpuWakeLock?.IsHeld == true)
            {
                _cpuWakeLock.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"No se pudieron liberar los recursos dedicados: {ex}");
        }
        finally
        {
            _wifiLock?.Dispose();
            _wifiLock = null;
            _cpuWakeLock?.Dispose();
            _cpuWakeLock = null;
        }
    }

    private void CreateNotificationChannel()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null)
        {
            return;
        }

        var channel = new NotificationChannel(
            ChannelId,
            "Escucha de Bardo",
            NotificationImportance.Low)
        {
            Description = "Mantiene activo el asistente de voz Bardo"
        };
        channel.EnableLights(false);
        channel.EnableVibration(false);
        channel.SetSound(null, null);
        manager.CreateNotificationChannel(channel);
    }

    private void StartAsForeground(string message)
    {
        Notification notification = BuildNotification(message);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void SetServiceStatus(string message)
    {
        CurrentStatus = message;
        Log.Info(LogTag, message);
        UpdateNotification(message);
        StatusChanged?.Invoke(message);
    }

    private void UpdateNotification(string message)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(message));
    }

    private Notification BuildNotification(string message)
    {
        var stopIntent = new Intent(this, typeof(BardoVoiceService));
        stopIntent.SetAction(ActionStop);

        PendingIntent? stopPendingIntent = PendingIntent.GetService(
            this,
            1,
            stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new Notification.Builder(this, ChannelId);
        builder.SetContentTitle("Bardo");
        builder.SetContentText(message);
        builder.SetSmallIcon(Android.Resource.Drawable.IcDialogInfo);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetCategory(Notification.CategoryService);
        builder.SetVisibility(NotificationVisibility.Secret);
        builder.AddAction(Android.Resource.Drawable.IcDelete, "Parar", stopPendingIntent);
        return builder.Build() ?? throw new InvalidOperationException("No se pudo crear la notificación de Bardo.");
    }
}
