using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Speech;
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
    private const long CommandPartialCommitDelayMilliseconds = 1_200;
    private const long CommandEndOfSpeechCommitDelayMilliseconds = 650;

    private readonly RelayCommandClient _relayClient = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Handler? _handler;
    private PowerManager.WakeLock? _cpuWakeLock;
    private WifiManager.WifiLock? _wifiLock;
    private SpeechRecognizer? _activeRecognizer;
    private SessionRecognitionListener? _activeListener;
    private Intent? _recognizerIntent;
    private BardoSettings _settings = BardoSettings.Default;
    private ListeningMode _mode = ListeningMode.WakeWord;
    private long _nextSessionId;
    private long? _activeSessionId;
    private ListeningMode? _activeSessionMode;
    private string? _commandPartialText;
    private int _commandRetryCount;
    private bool _destroyed;
    private bool _commandInFlight;
    private bool _standardRecognizerAvailable;
    private bool _onDeviceRecognizerAvailable;

    public static bool IsRunning { get; private set; }
    public static string CurrentStatus { get; private set; } = "detenido";
    public static float LastRmsDb { get; private set; } = float.NaN;
    public static string LastRecognizerEvent { get; private set; } = "sin eventos";
    public static event Action<string>? StatusChanged;

    public override void OnCreate()
    {
        base.OnCreate();
        IsRunning = true;
        CurrentStatus = "iniciando servicio";
        LastRmsDb = float.NaN;
        LastRecognizerEvent = "iniciando";

        try
        {
            _handler = new Handler(Looper.MainLooper!);
            _settings = BardoSettingsStore.Load(this);
            CreateNotificationChannel();
            StartAsForeground("Iniciando reconocimiento de voz…");
            AcquireDedicatedResourceLocks();

            _onDeviceRecognizerAvailable =
                Build.VERSION.SdkInt >= BuildVersionCodes.S &&
                SpeechRecognizer.IsOnDeviceRecognitionAvailable(this);
            _standardRecognizerAvailable = SpeechRecognizer.IsRecognitionAvailable(this);

            Log.Info(
                LogTag,
                $"Reconocimiento local={_onDeviceRecognizerAvailable}, estándar={_standardRecognizerAvailable}");

            if (!_onDeviceRecognizerAvailable && !_standardRecognizerAvailable)
            {
                SetServiceStatus("No hay ningún servicio de reconocimiento de voz disponible");
                return;
            }

            _recognizerIntent = BuildRecognizerIntent();
            SetServiceStatus($"Esperando «{_settings.WakeWord}» · {RecognizerLabel}");
            ScheduleListen(300, "inicio del servicio");
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
        if (_activeSessionId is null && !_commandInFlight)
        {
            if (_mode == ListeningMode.WakeWord)
            {
                SetServiceStatus($"Esperando «{_settings.WakeWord}» · {RecognizerLabel}");
            }

            ScheduleListen(150, "OnStartCommand sin sesión activa");
        }

        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        IsRunning = false;
        CurrentStatus = "detenido";
        LastRecognizerEvent = "servicio detenido";
        StatusChanged?.Invoke(CurrentStatus);
        _destroyed = true;
        _shutdown.Cancel();
        _handler?.RemoveCallbacksAndMessages(null);

        if (_activeSessionId is long sessionId)
        {
            CloseSession(sessionId, "destrucción del servicio", cancel: true);
        }

        _handler = null;
        ReleaseDedicatedResourceLocks();
        _recognizerIntent?.Dispose();
        _recognizerIntent = null;
        _shutdown.Dispose();
        base.OnDestroy();
    }

    private string RecognizerLabel => _standardRecognizerAvailable ? "sistema" : "local";

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

            var wifiManager =
                (WifiManager?)ApplicationContext?.GetSystemService(WifiService);
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
            Log.Warn(LogTag, $"No se pudieron liberar todos los recursos dedicados: {ex}");
        }
        finally
        {
            _wifiLock?.Dispose();
            _wifiLock = null;
            _cpuWakeLock?.Dispose();
            _cpuWakeLock = null;
        }
    }

    private Intent BuildRecognizerIntent()
    {
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraLanguage, "es-ES");
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 3);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputMinimumLengthMillis, 250L);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, 600L);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputPossiblyCompleteSilenceLengthMillis, 600L);
        return intent;
    }

    private void ScheduleListen(long delayMilliseconds, string reason)
    {
        if (_destroyed || _recognizerIntent is null || _handler is null || _commandInFlight)
        {
            return;
        }

        _handler.RemoveCallbacksAndMessages(null);
        Log.Debug(LogTag, $"Programando escucha mode={_mode} delay={delayMilliseconds} ms reason={reason}");
        _handler.PostDelayed(StartNewSession, delayMilliseconds);
    }

    private void StartNewSession()
    {
        if (_destroyed ||
            _commandInFlight ||
            _activeSessionId is not null ||
            _recognizerIntent is null)
        {
            return;
        }

        var sessionId = ++_nextSessionId;
        var sessionMode = _mode;
        SpeechRecognizer? recognizer = null;

        try
        {
            recognizer = CreateRecognizer();
            var listener = new SessionRecognitionListener(this, sessionId, sessionMode);
            recognizer.SetRecognitionListener(listener);

            _activeSessionId = sessionId;
            _activeSessionMode = sessionMode;
            _activeRecognizer = recognizer;
            _activeListener = listener;
            _commandPartialText = null;

            Log.Info(LogTag, $"{SessionLabel(sessionId, sessionMode)} create recognizer={RecognizerLabel}");
            LastRecognizerEvent = $"S{sessionId} StartListening ({sessionMode})";

            if (sessionMode == ListeningMode.Command)
            {
                SetServiceStatus("Bardo · habla ahora");
            }

            Log.Info(LogTag, $"{SessionLabel(sessionId, sessionMode)} StartListening");
            recognizer.StartListening(_recognizerIntent);
        }
        catch (Exception ex)
        {
            LastRecognizerEvent = $"S{sessionId} StartListening falló: {ex.GetType().Name}";
            Log.Warn(LogTag, $"{SessionLabel(sessionId, sessionMode)} StartListening falló: {ex}");

            if (_activeSessionId == sessionId)
            {
                CloseSession(sessionId, "StartListening falló", cancel: true);
            }
            else
            {
                DestroyRecognizer(recognizer, sessionId, sessionMode, "fallo antes de activar sesión", cancel: true);
            }

            SetServiceStatus($"Error iniciando micrófono: {ex.GetType().Name}");
            ScheduleListen(1_200, "reintento tras fallo de StartListening");
        }
    }

    private SpeechRecognizer CreateRecognizer()
    {
        SpeechRecognizer? recognizer;

        if (_standardRecognizerAvailable)
        {
            recognizer = SpeechRecognizer.CreateSpeechRecognizer(this);
        }
        else
        {
            recognizer = SpeechRecognizer.CreateOnDeviceSpeechRecognizer(this);
        }

        return recognizer ?? throw new InvalidOperationException("Android devolvió un SpeechRecognizer nulo.");
    }

    private bool IsCurrentSession(long sessionId, ListeningMode sessionMode, string callback)
    {
        var isCurrent =
            !_destroyed &&
            _activeSessionId == sessionId &&
            _activeSessionMode == sessionMode &&
            _mode == sessionMode;

        if (!isCurrent)
        {
            var active = _activeSessionId is long activeId
                ? $"S{activeId}/{_activeSessionMode}"
                : "ninguna";
            Log.Warn(
                LogTag,
                $"{SessionLabel(sessionId, sessionMode)} {callback} IGNORADO; active={active}, mode={_mode}, destroyed={_destroyed}");
        }

        return isCurrent;
    }

    private void HandleReadyForSpeech(long sessionId, ListeningMode sessionMode)
    {
        if (!IsCurrentSession(sessionId, sessionMode, nameof(IRecognitionListener.OnReadyForSpeech)))
        {
            return;
        }

        LastRecognizerEvent = $"S{sessionId} micrófono listo · mode={sessionMode}";
        Log.Info(LogTag, $"{SessionLabel(sessionId, sessionMode)} OnReadyForSpeech");

        if (sessionMode == ListeningMode.Command)
        {
            SetServiceStatus("Bardo · habla ahora");
        }
    }

    private void HandleBeginningOfSpeech(long sessionId, ListeningMode sessionMode)
    {
        if (!IsCurrentSession(sessionId, sessionMode, nameof(IRecognitionListener.OnBeginningOfSpeech)))
        {
            return;
        }

        LastRecognizerEvent = $"S{sessionId} voz detectada · mode={sessionMode}";
        Log.Info(LogTag, $"{SessionLabel(sessionId, sessionMode)} OnBeginningOfSpeech");

        if (sessionMode == ListeningMode.Command)
        {
            SetServiceStatus("Escuchando comando…");
        }
    }

    private void HandlePartialResults(long sessionId, ListeningMode sessionMode, Bundle? results)
    {
        if (!IsCurrentSession(sessionId, sessionMode, nameof(IRecognitionListener.OnPartialResults)))
        {
            return;
        }

        var alternatives = GetRecognitionResults(results);
        var text = alternatives.FirstOrDefault();
        LastRecognizerEvent = $"S{sessionId} parcial: {text ?? "<vacío>"}";
        Log.Debug(
            LogTag,
            $"{SessionLabel(sessionId, sessionMode)} OnPartialResults alternatives={FormatAlternatives(alternatives)}");

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (sessionMode == ListeningMode.WakeWord)
        {
            var wakeMatch = FindWakeWordResult(alternatives);
            if (wakeMatch is not null)
            {
                EnterCommandMode(sessionId, wakeMatch, "OnPartialResults");
            }

            return;
        }

        _commandPartialText = text.Trim();
        SchedulePartialCommandCommit(sessionId, CommandPartialCommitDelayMilliseconds);
    }

    private void HandleResults(long sessionId, ListeningMode sessionMode, Bundle? results)
    {
        if (!IsCurrentSession(sessionId, sessionMode, nameof(IRecognitionListener.OnResults)))
        {
            return;
        }

        var alternatives = GetRecognitionResults(results);
        var text = alternatives.FirstOrDefault();
        LastRecognizerEvent = $"S{sessionId} resultado: {text ?? "<vacío>"}";
        Log.Info(
            LogTag,
            $"{SessionLabel(sessionId, sessionMode)} OnResults alternatives={FormatAlternatives(alternatives)}");

        if (sessionMode == ListeningMode.WakeWord)
        {
            var wakeMatch = FindWakeWordResult(alternatives);
            if (wakeMatch is not null)
            {
                EnterCommandMode(sessionId, wakeMatch, "OnResults");
                return;
            }

            CloseSession(sessionId, "resultado de wake word sin coincidencia", cancel: false);
            ScheduleListen(200, "continuar esperando wake word");
            return;
        }

        var command = string.IsNullOrWhiteSpace(text) ? _commandPartialText : text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            CloseSession(sessionId, "resultado de comando vacío", cancel: false);
            RetryOrLeaveCommandMode("No he entendido el comando");
            return;
        }

        DispatchCommand(sessionId, command, "OnResults", cancelRecognizer: false);
    }

    private void HandleError(long sessionId, ListeningMode sessionMode, SpeechRecognizerError error)
    {
        if (!IsCurrentSession(sessionId, sessionMode, nameof(IRecognitionListener.OnError)))
        {
            return;
        }

        LastRecognizerEvent = $"S{sessionId} error: {error} · mode={sessionMode}";
        Log.Warn(LogTag, $"{SessionLabel(sessionId, sessionMode)} OnError error={error}");
        CloseSession(sessionId, $"OnError {error}", cancel: false);

        if (sessionMode == ListeningMode.Command)
        {
            RetryOrLeaveCommandMode(error is SpeechRecognizerError.NoMatch or SpeechRecognizerError.SpeechTimeout
                ? "No te he oído"
                : $"Error de voz: {error}");
            return;
        }

        if (error is not SpeechRecognizerError.NoMatch and not SpeechRecognizerError.SpeechTimeout)
        {
            SetServiceStatus($"Voz: {error} · reintentando");
        }

        ScheduleListen(error == SpeechRecognizerError.RecognizerBusy ? 1_400 : 450, $"OnError {error}");
    }

    private void HandleEndOfSpeech(long sessionId, ListeningMode sessionMode)
    {
        if (!IsCurrentSession(sessionId, sessionMode, nameof(IRecognitionListener.OnEndOfSpeech)))
        {
            return;
        }

        LastRecognizerEvent = $"S{sessionId} fin de voz · mode={sessionMode}";
        Log.Info(LogTag, $"{SessionLabel(sessionId, sessionMode)} OnEndOfSpeech");

        if (sessionMode == ListeningMode.Command && !string.IsNullOrWhiteSpace(_commandPartialText))
        {
            SchedulePartialCommandCommit(sessionId, CommandEndOfSpeechCommitDelayMilliseconds);
        }
    }

    private void HandleRmsChanged(long sessionId, ListeningMode sessionMode, float rmsdB)
    {
        if (_activeSessionId == sessionId && _activeSessionMode == sessionMode)
        {
            LastRmsDb = rmsdB;
        }
    }

    private void EnterCommandMode(long wakeSessionId, string wakeText, string source)
    {
        if (!IsCurrentSession(wakeSessionId, ListeningMode.WakeWord, source))
        {
            return;
        }

        LastRecognizerEvent = $"S{wakeSessionId} Bardo detectado: {wakeText}";
        Log.Info(LogTag, $"{SessionLabel(wakeSessionId, ListeningMode.WakeWord)} wake detectada via {source}");

        // La sesión queda invalidada ANTES de Cancel/Destroy. Así, cualquier OnError u
        // OnResults tardío del recognizer anterior conserva su id, pero no puede tocar
        // el estado de la nueva sesión de comando.
        CloseSession(wakeSessionId, $"transición WakeWord -> Command via {source}", cancel: true);
        _mode = ListeningMode.Command;
        _commandRetryCount = 0;
        _commandPartialText = null;
        SetServiceStatus("Bardo detectado · preparando micrófono…");
        ScheduleListen(350, "nueva sesión exclusiva para el comando");
    }

    private void SchedulePartialCommandCommit(long sessionId, long delayMilliseconds)
    {
        if (_handler is null)
        {
            return;
        }

        _handler.RemoveCallbacksAndMessages(null);
        Log.Debug(LogTag, $"S{sessionId} [Command] programando fallback parcial en {delayMilliseconds} ms");
        _handler.PostDelayed(() => CommitPartialCommand(sessionId), delayMilliseconds);
    }

    private void CommitPartialCommand(long sessionId)
    {
        if (!IsCurrentSession(sessionId, ListeningMode.Command, "fallback de parcial"))
        {
            return;
        }

        var command = _commandPartialText?.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        DispatchCommand(sessionId, command, "fallback de OnPartialResults", cancelRecognizer: true);
    }

    private void DispatchCommand(
        long sessionId,
        string command,
        string source,
        bool cancelRecognizer)
    {
        if (!IsCurrentSession(sessionId, ListeningMode.Command, source) || _commandInFlight)
        {
            return;
        }

        var normalizedCommand = command.Trim();
        if (normalizedCommand.Length == 0)
        {
            return;
        }

        // Invalidar/cerrar antes del POST garantiza que un final posterior al parcial,
        // o cualquier callback duplicado, no puede ejecutar el comando por segunda vez.
        CloseSession(sessionId, $"comando capturado via {source}", cancelRecognizer);
        _commandInFlight = true;
        _commandPartialText = null;
        SetServiceStatus($"Ejecutando: {normalizedCommand}");
        Log.Info(LogTag, $"S{sessionId} [Command] SEND once source={source} text={normalizedCommand}");
        _ = ExecuteCommandAndResumeAsync(sessionId, normalizedCommand);
    }

    private async Task ExecuteCommandAndResumeAsync(long sessionId, string command)
    {
        var result = await _relayClient.SendAsync(_settings, command, _shutdown.Token).ConfigureAwait(false);
        if (_destroyed)
        {
            return;
        }

        _handler?.Post(() =>
        {
            if (_destroyed)
            {
                return;
            }

            Log.Info(LogTag, $"S{sessionId} [Command] relay success={result.Success}: {result.Message}");
            SetServiceStatus(result.Success
                ? $"Hecho · {result.Message}"
                : $"Error · {result.Message}");

            _handler?.PostDelayed(ReturnToWakeWord, 900);
        });
    }

    private void RetryOrLeaveCommandMode(string reason)
    {
        if (_commandRetryCount == 0)
        {
            _commandRetryCount++;
            SetServiceStatus($"{reason} · habla ahora");
            ScheduleListen(300, "segundo intento de comando");
            return;
        }

        SetServiceStatus($"{reason} · volviendo a esperar Bardo");
        _handler?.PostDelayed(ReturnToWakeWord, 700);
    }

    private void ReturnToWakeWord()
    {
        if (_destroyed)
        {
            return;
        }

        if (_activeSessionId is long sessionId)
        {
            CloseSession(sessionId, "retorno forzado a WakeWord", cancel: true);
        }

        _commandInFlight = false;
        _commandPartialText = null;
        _commandRetryCount = 0;
        _mode = ListeningMode.WakeWord;
        SetServiceStatus($"Esperando «{_settings.WakeWord}» · {RecognizerLabel}");
        ScheduleListen(200, "retorno a WakeWord");
    }

    private void CloseSession(long sessionId, string reason, bool cancel)
    {
        if (_activeSessionId != sessionId)
        {
            Log.Warn(LogTag, $"S{sessionId} CloseSession ignorado; ya no es la sesión activa ({reason})");
            return;
        }

        var recognizer = _activeRecognizer;
        var sessionMode = _activeSessionMode ?? _mode;

        // Es deliberado que primero se borre la identidad activa: Cancel y Destroy
        // pueden producir callbacks síncronos o encolados en algunos fabricantes.
        _activeSessionId = null;
        _activeSessionMode = null;
        _activeRecognizer = null;
        _activeListener = null;
        _handler?.RemoveCallbacksAndMessages(null);

        Log.Info(LogTag, $"{SessionLabel(sessionId, sessionMode)} close reason={reason}");
        DestroyRecognizer(recognizer, sessionId, sessionMode, reason, cancel);
    }

    private static void DestroyRecognizer(
        SpeechRecognizer? recognizer,
        long sessionId,
        ListeningMode sessionMode,
        string reason,
        bool cancel)
    {
        if (recognizer is null)
        {
            return;
        }

        if (cancel)
        {
            try
            {
                Log.Info(LogTag, $"{SessionLabel(sessionId, sessionMode)} Cancel reason={reason}");
                recognizer.Cancel();
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"{SessionLabel(sessionId, sessionMode)} Cancel falló: {ex.Message}");
            }
        }

        try
        {
            Log.Info(LogTag, $"{SessionLabel(sessionId, sessionMode)} Destroy reason={reason}");
            recognizer.Destroy();
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"{SessionLabel(sessionId, sessionMode)} Destroy falló: {ex.Message}");
        }
    }

    private string? FindWakeWordResult(IEnumerable<string> alternatives) =>
        alternatives.FirstOrDefault(MatchesWakeWord);

    private bool MatchesWakeWord(string text)
    {
        var wakeWord = _settings.WakeWord.Trim();
        if (wakeWord.Length == 0)
        {
            return false;
        }

        if (ContainsWholePhrase(text, wakeWord))
        {
            return true;
        }

        // En este OPPO, el reconocimiento ha devuelto variantes fonéticas al oír
        // «Bardo». La equivalencia se limita a la wake word predeterminada para no
        // relajar otras palabras de activación configuradas por el usuario.
        return wakeWord.Equals("bardo", StringComparison.OrdinalIgnoreCase) &&
               (ContainsWholePhrase(text, "pardo") ||
                ContainsWholePhrase(text, "vardo") ||
                ContainsWholePhrase(text, "borde"));
    }

    private static bool ContainsWholePhrase(string text, string phrase) =>
        Regex.IsMatch(
            text,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IReadOnlyList<string> GetRecognitionResults(Bundle? bundle)
    {
        var values = bundle?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
        return values is { Count: > 0 }
            ? values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray()
            : [];
    }

    private static string FormatAlternatives(IReadOnlyList<string> alternatives) =>
        alternatives.Count == 0 ? "<vacío>" : string.Join(" | ", alternatives);

    private static string SessionLabel(long sessionId, ListeningMode mode) => $"S{sessionId} [{mode}]";

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
        var notification = BuildNotification(message);

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

        var stopPendingIntent = PendingIntent.GetService(
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

    private sealed class SessionRecognitionListener(
        BardoVoiceService owner,
        long sessionId,
        ListeningMode sessionMode) : Java.Lang.Object, IRecognitionListener
    {
        public void OnReadyForSpeech(Bundle? @params) =>
            owner.HandleReadyForSpeech(sessionId, sessionMode);

        public void OnBeginningOfSpeech() =>
            owner.HandleBeginningOfSpeech(sessionId, sessionMode);

        public void OnRmsChanged(float rmsdB) =>
            owner.HandleRmsChanged(sessionId, sessionMode, rmsdB);

        public void OnBufferReceived(byte[]? buffer) { }

        public void OnEndOfSpeech() =>
            owner.HandleEndOfSpeech(sessionId, sessionMode);

        public void OnError(SpeechRecognizerError error) =>
            owner.HandleError(sessionId, sessionMode, error);

        public void OnResults(Bundle? results) =>
            owner.HandleResults(sessionId, sessionMode, results);

        public void OnPartialResults(Bundle? partialResults) =>
            owner.HandlePartialResults(sessionId, sessionMode, partialResults);

        public void OnEvent(int eventType, Bundle? @params) { }
        public void OnSegmentResults(Bundle segmentResults) { }
        public void OnEndOfSegmentedSession() { }
        public void OnLanguageDetection(Bundle results) { }
    }

    private enum ListeningMode
    {
        WakeWord,
        Command
    }
}
