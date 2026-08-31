using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Speech;
using Android.Util;
using Bardo.Mobile.Infrastructure;

namespace Bardo.Mobile;

[Service(
    Name = "com.treska23.bardo.BardoVoiceService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeMicrophone)]
public sealed class BardoVoiceService : Service, IRecognitionListener
{
    private const string ChannelId = "bardo-listening";
    private const int NotificationId = 1701;
    private const string ActionStop = "com.treska23.bardo.STOP";
    private const string LogTag = "BardoVoice";

    private readonly RelayCommandClient _relayClient = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Handler? _handler;
    private SpeechRecognizer? _recognizer;
    private Intent? _recognizerIntent;
    private BardoSettings _settings = BardoSettings.Default;
    private ListeningMode _mode = ListeningMode.WakeWord;
    private bool _destroyed;
    private bool _waitingForResult;
    private bool _usingOnDeviceRecognizer;
    private bool _recreatingRecognizer;

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

            if (!CreateRecognizer())
            {
                return;
            }

            SetServiceStatus($"Esperando «{_settings.WakeWord}» · {RecognizerLabel}");
            ScheduleListen(300);
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
        if (_recognizer is not null && _mode == ListeningMode.WakeWord)
        {
            SetServiceStatus($"Esperando «{_settings.WakeWord}» · {RecognizerLabel}");
            ScheduleListen(150);
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
        DestroyRecognizer();
        _shutdown.Dispose();
        base.OnDestroy();
    }

    private string RecognizerLabel => _usingOnDeviceRecognizer ? "local" : "sistema";

    private bool CreateRecognizer()
    {
        var onDeviceAvailable =
            Build.VERSION.SdkInt >= BuildVersionCodes.S &&
            SpeechRecognizer.IsOnDeviceRecognitionAvailable(this);
        var standardAvailable = SpeechRecognizer.IsRecognitionAvailable(this);

        Log.Info(LogTag, $"Reconocimiento local={onDeviceAvailable}, estándar={standardAvailable}");

        if (!onDeviceAvailable && !standardAvailable)
        {
            SetServiceStatus("No hay ningún servicio de reconocimiento de voz disponible");
            return false;
        }

        DestroyRecognizer();

        // Para este OPPO priorizamos el reconocedor estándar. El reconocedor local
        // anuncia soporte pero se ha mostrado menos fiable con español.
        if (standardAvailable)
        {
            _recognizer = SpeechRecognizer.CreateSpeechRecognizer(this);
            _usingOnDeviceRecognizer = false;
        }
        else
        {
            _recognizer = SpeechRecognizer.CreateOnDeviceSpeechRecognizer(this);
            _usingOnDeviceRecognizer = true;
        }

        if (_recognizer is null)
        {
            SetServiceStatus("No se pudo crear el reconocedor de voz");
            return false;
        }

        _recognizer.SetRecognitionListener(this);
        _recognizerIntent = BuildRecognizerIntent();
        _waitingForResult = false;
        return true;
    }

    private void DestroyRecognizer()
    {
        try
        {
            _recognizer?.Cancel();
            _recognizer?.Destroy();
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"DestroyRecognizer: {ex.Message}");
        }

        _recognizer = null;
        _recognizerIntent = null;
        _waitingForResult = false;
    }

    private Intent BuildRecognizerIntent()
    {
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraLanguage, "es-ES");
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 3);
        return intent;
    }

    private void ScheduleListen(long delayMilliseconds)
    {
        if (_destroyed || _recognizer is null || _recognizerIntent is null || _handler is null)
        {
            return;
        }

        _handler.RemoveCallbacksAndMessages(null);
        _handler.PostDelayed(StartListening, delayMilliseconds);
    }

    private void StartListening()
    {
        if (_destroyed || _recreatingRecognizer || _waitingForResult || _recognizer is null || _recognizerIntent is null)
        {
            return;
        }

        try
        {
            _waitingForResult = true;
            LastRecognizerEvent = $"StartListening ({_mode})";
            _recognizer.StartListening(_recognizerIntent);
            Log.Debug(LogTag, $"StartListening mode={_mode}");
        }
        catch (Exception ex)
        {
            _waitingForResult = false;
            LastRecognizerEvent = $"StartListening falló: {ex.GetType().Name}";
            Log.Warn(LogTag, $"StartListening falló: {ex}");
            SetServiceStatus($"Error iniciando micrófono: {ex.GetType().Name}");
            ScheduleListen(1200);
        }
    }

    public void OnResults(Bundle? results)
    {
        if (_recreatingRecognizer)
        {
            return;
        }

        _waitingForResult = false;
        var text = GetBestResult(results);
        LastRecognizerEvent = $"resultado: {text ?? "<vacío>"}";
        Log.Info(LogTag, $"Resultado: {text ?? "<vacío>"} · mode={_mode}");

        if (string.IsNullOrWhiteSpace(text))
        {
            ScheduleListen(250);
            return;
        }

        if (_mode == ListeningMode.WakeWord)
        {
            if (!TryExtractWakeCommand(text, out var command))
            {
                ScheduleListen(200);
                return;
            }

            if (!string.IsNullOrWhiteSpace(command))
            {
                _ = ExecuteCommandAndResumeAsync(command);
                return;
            }

            BeginCommandSession();
            return;
        }

        _ = ExecuteCommandAndResumeAsync(text.Trim());
    }

    public void OnPartialResults(Bundle? partialResults)
    {
        if (_recreatingRecognizer)
        {
            return;
        }

        var text = GetBestResult(partialResults);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        LastRecognizerEvent = $"parcial: {text}";
        Log.Debug(LogTag, $"Parcial: {text} · mode={_mode}");

        if (_mode != ListeningMode.WakeWord || !ContainsWakeWord(text))
        {
            return;
        }

        LastRecognizerEvent = $"wake word detectada: {text}";
        BeginCommandSession();
    }

    private void BeginCommandSession()
    {
        if (_recreatingRecognizer || _destroyed)
        {
            return;
        }

        _mode = ListeningMode.Command;
        _recreatingRecognizer = true;
        SetServiceStatus("Bardo detectado · preparando escucha del comando…");

        // En este OPPO reutilizar el SpeechRecognizer justo después de Cancel puede
        // dejar la segunda sesión muda. Creamos una instancia nueva para el comando.
        DestroyRecognizer();

        if (_handler is null)
        {
            _recreatingRecognizer = false;
            return;
        }

        _handler.RemoveCallbacksAndMessages(null);
        _handler.PostDelayed(() =>
        {
            if (_destroyed)
            {
                return;
            }

            try
            {
                if (!CreateRecognizer())
                {
                    _recreatingRecognizer = false;
                    return;
                }

                _recreatingRecognizer = false;
                SetServiceStatus("Bardo detectado · escuchando comando…");
                ScheduleListen(250);
            }
            catch (Exception ex)
            {
                _recreatingRecognizer = false;
                LastRecognizerEvent = $"recrear falló: {ex.GetType().Name}";
                SetServiceStatus($"Error preparando comando: {ex.GetType().Name}");
                Log.Error(LogTag, ex.ToString());
                ReturnToWakeWord();
            }
        }, 700);
    }

    public void OnError(SpeechRecognizerError error)
    {
        if (_recreatingRecognizer || _destroyed)
        {
            return;
        }

        _waitingForResult = false;
        LastRecognizerEvent = $"error: {error} · mode={_mode}";
        Log.Warn(LogTag, $"SpeechRecognizer error: {error} · mode={_mode}");

        if (error is not SpeechRecognizerError.NoMatch and not SpeechRecognizerError.SpeechTimeout)
        {
            SetServiceStatus($"Voz: {error} · reintentando");
        }

        ScheduleListen(error == SpeechRecognizerError.RecognizerBusy ? 1400 : 450);
    }

    private async Task ExecuteCommandAndResumeAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            ReturnToWakeWord();
            return;
        }

        SetServiceStatus($"Ejecutando: {command}");
        Log.Info(LogTag, $"Enviando comando: {command}");

        var result = await _relayClient.SendAsync(_settings, command, _shutdown.Token);
        if (_destroyed)
        {
            return;
        }

        Log.Info(LogTag, $"Relay success={result.Success}: {result.Message}");
        SetServiceStatus(result.Success
            ? $"Hecho · {result.Message}"
            : $"Error · {result.Message}");

        try
        {
            await Task.Delay(900, _shutdown.Token);
        }
        catch (System.OperationCanceledException)
        {
            return;
        }

        ReturnToWakeWord();
    }

    private void ReturnToWakeWord()
    {
        _mode = ListeningMode.WakeWord;
        _recreatingRecognizer = false;
        _waitingForResult = false;
        SetServiceStatus($"Esperando «{_settings.WakeWord}» · {RecognizerLabel}");
        ScheduleListen(200);
    }

    private bool TryExtractWakeCommand(string text, out string command)
    {
        command = string.Empty;
        var wakeWord = _settings.WakeWord.Trim();
        if (wakeWord.Length == 0)
        {
            return false;
        }

        var index = text.IndexOf(wakeWord, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var remainderIndex = index + wakeWord.Length;
        if (remainderIndex < text.Length)
        {
            command = text[remainderIndex..].Trim(' ', ',', '.', ':', ';', '-', '—');
        }

        return true;
    }

    private bool ContainsWakeWord(string text) =>
        text.Contains(_settings.WakeWord.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? GetBestResult(Bundle? bundle)
    {
        var values = bundle?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
        return values is { Count: > 0 } ? values[0] : null;
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

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("Bardo")
            .SetContentText(message)
            .SetSmallIcon(Android.Resource.Drawable.IcDialogInfo)
            .SetOngoing(true)
            .AddAction(Android.Resource.Drawable.IcDelete, "Parar", stopPendingIntent)
            .Build();
    }

    public void OnReadyForSpeech(Bundle? @params)
    {
        LastRecognizerEvent = $"micrófono listo · mode={_mode}";
        Log.Debug(LogTag, LastRecognizerEvent);

        if (_mode == ListeningMode.Command)
        {
            SetServiceStatus("Bardo detectado · habla ahora");
        }
    }

    public void OnBeginningOfSpeech()
    {
        LastRecognizerEvent = $"voz detectada · mode={_mode}";
        Log.Debug(LogTag, LastRecognizerEvent);

        if (_mode == ListeningMode.Command)
        {
            SetServiceStatus("Escuchando comando…");
        }
    }

    public void OnRmsChanged(float rmsdB)
    {
        LastRmsDb = rmsdB;
    }

    public void OnBufferReceived(byte[]? buffer) { }

    public void OnEndOfSpeech()
    {
        LastRecognizerEvent = $"fin de voz · mode={_mode}";
        Log.Debug(LogTag, LastRecognizerEvent);
    }

    public void OnEvent(int eventType, Bundle? @params) { }
    public void OnSegmentResults(Bundle segmentResults) { }
    public void OnEndOfSegmentedSession() { }
    public void OnLanguageDetection(Bundle results) { }

    private enum ListeningMode
    {
        WakeWord,
        Command
    }
}
