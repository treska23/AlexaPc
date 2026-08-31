using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Speech;
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

    private readonly RelayCommandClient _relayClient = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Handler? _handler;
    private SpeechRecognizer? _recognizer;
    private Intent? _recognizerIntent;
    private BardoSettings _settings = BardoSettings.Default;
    private ListeningMode _mode = ListeningMode.WakeWord;
    private bool _destroyed;
    private bool _waitingForResult;

    public override void OnCreate()
    {
        base.OnCreate();

        _handler = new Handler(Looper.MainLooper!);
        _settings = BardoSettingsStore.Load(this);
        CreateNotificationChannel();
        StartAsForeground($"Esperando «{_settings.WakeWord}»");

        if (!SpeechRecognizer.IsRecognitionAvailable(this))
        {
            UpdateNotification("No hay reconocimiento de voz disponible");
            return;
        }

        _recognizer = SpeechRecognizer.CreateSpeechRecognizer(this);
        _recognizer.SetRecognitionListener(this);
        _recognizerIntent = BuildRecognizerIntent();
        ScheduleListen(300);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        _settings = BardoSettingsStore.Load(this);
        UpdateNotification($"Esperando «{_settings.WakeWord}»");
        ScheduleListen(150);
        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        _destroyed = true;
        _shutdown.Cancel();
        _handler?.RemoveCallbacksAndMessages(null);

        try
        {
            _recognizer?.Cancel();
            _recognizer?.Destroy();
        }
        catch
        {
            // Android puede cerrar el reconocedor mientras entrega un callback.
        }

        _recognizer = null;
        _shutdown.Dispose();
        base.OnDestroy();
    }

    private Intent BuildRecognizerIntent()
    {
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraLanguage, "es-ES");
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
        intent.PutExtra(RecognizerIntent.ExtraPreferOffline, true);
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
        if (_destroyed || _waitingForResult || _recognizer is null || _recognizerIntent is null)
        {
            return;
        }

        try
        {
            _waitingForResult = true;
            _recognizer.StartListening(_recognizerIntent);
        }
        catch
        {
            _waitingForResult = false;
            ScheduleListen(1000);
        }
    }

    public void OnResults(Bundle? results)
    {
        _waitingForResult = false;
        var text = GetBestResult(results);

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

            _mode = ListeningMode.Command;
            UpdateNotification("Te escucho…");
            ScheduleListen(150);
            return;
        }

        _ = ExecuteCommandAndResumeAsync(text.Trim());
    }

    public void OnPartialResults(Bundle? partialResults)
    {
        if (_mode != ListeningMode.WakeWord)
        {
            return;
        }

        var text = GetBestResult(partialResults);
        if (!string.IsNullOrWhiteSpace(text) && ContainsWakeWord(text))
        {
            UpdateNotification("Bardo detectado…");
        }
    }

    public void OnError(SpeechRecognizerError error)
    {
        _waitingForResult = false;

        if (_destroyed)
        {
            return;
        }

        var delay = error == SpeechRecognizerError.RecognizerBusy ? 1200 : 350;
        ScheduleListen(delay);
    }

    private async Task ExecuteCommandAndResumeAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            _mode = ListeningMode.WakeWord;
            ScheduleListen(200);
            return;
        }

        UpdateNotification($"Ejecutando: {command}");

        var result = await _relayClient.SendAsync(_settings, command, _shutdown.Token);
        if (_destroyed)
        {
            return;
        }

        UpdateNotification(result.Success
            ? $"Hecho · {result.Message}"
            : $"Error · {result.Message}");

        try
        {
            await Task.Delay(900, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _mode = ListeningMode.WakeWord;
        UpdateNotification($"Esperando «{_settings.WakeWord}»");
        ScheduleListen(150);
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
            .SetSmallIcon(Android.Resource.Drawable.IcBtnSpeakNow)
            .SetOngoing(true)
            .AddAction(Android.Resource.Drawable.IcDelete, "Parar", stopPendingIntent)
            .Build();
    }

    public void OnReadyForSpeech(Bundle? @params) { }
    public void OnBeginningOfSpeech() { }
    public void OnRmsChanged(float rmsdB) { }
    public void OnBufferReceived(byte[]? buffer) { }
    public void OnEndOfSpeech() { }
    public void OnEvent(int eventType, Bundle? @params) { }

    private enum ListeningMode
    {
        WakeWord,
        Command
    }
}
