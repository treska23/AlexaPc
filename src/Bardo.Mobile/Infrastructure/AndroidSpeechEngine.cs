using Android.Content;
using Android.OS;
using Android.Speech;
using Android.Util;

namespace Bardo.Mobile.Infrastructure;

/// <summary>
/// Mantiene sesiones cortas del reconocedor local de Android. Usa resultados
/// parciales para reaccionar a «Bardo» sin esperar al cierre completo de frase.
/// </summary>
internal sealed class AndroidSpeechEngine : Java.Lang.Object, IRecognitionListener, IDisposable
{
    private const string LogTag = "BardoVoice";
    private const long PartialCommandDelayMilliseconds = 850;
    private const long EndOfSpeechCommandDelayMilliseconds = 250;

    private readonly Context _context;
    private readonly Handler _handler;
    private readonly Func<BardoSettings> _settingsProvider;
    private readonly Func<string, CancellationToken, Task> _executeCommand;
    private readonly Action _playWakeAcknowledgement;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _setEvent;
    private readonly Action<float> _setRms;
    private readonly CancellationTokenSource _lifetime = new();

    private SpeechRecognizer? _recognizer;
    private Intent? _recognizerIntent;
    private long _nextSessionId;
    private long? _activeSessionId;
    private ListeningMode _mode = ListeningMode.WakeWord;
    private string? _partialCommand;
    private int _commandRetryCount;
    private bool _commandInFlight;
    private bool _started;
    private bool _disposed;

    public AndroidSpeechEngine(
        Context context,
        Func<BardoSettings> settingsProvider,
        Func<string, CancellationToken, Task> executeCommand,
        Action playWakeAcknowledgement,
        Action<string> setStatus,
        Action<string> setEvent,
        Action<float> setRms)
    {
        _context = context.ApplicationContext ?? context;
        _settingsProvider = settingsProvider;
        _executeCommand = executeCommand;
        _playWakeAcknowledgement = playWakeAcknowledgement;
        _setStatus = setStatus;
        _setEvent = setEvent;
        _setRms = setRms;
        _handler = new Handler(Looper.MainLooper!);
    }

    public static bool IsAvailable(Context context) =>
        Build.VERSION.SdkInt >= BuildVersionCodes.S &&
        SpeechRecognizer.IsOnDeviceRecognitionAvailable(context);

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _handler.Post(() =>
        {
            SetWaitingStatus();
            ScheduleListen(100, "inicio");
        });
    }

    private void ScheduleListen(long delayMilliseconds, string reason)
    {
        if (_disposed || _commandInFlight || _activeSessionId is not null)
        {
            return;
        }

        _handler.RemoveCallbacksAndMessages(null);
        Log.Debug(LogTag, $"Android voice: escucha {_mode} en {delayMilliseconds} ms · {reason}");
        _handler.PostDelayed(StartNewSession, delayMilliseconds);
    }

    private void StartNewSession()
    {
        if (_disposed || _commandInFlight || _activeSessionId is not null)
        {
            return;
        }

        long sessionId = ++_nextSessionId;
        try
        {
            if (!IsAvailable(_context))
            {
                throw new InvalidOperationException("El reconocedor local de Android no está disponible.");
            }

            _recognizer = SpeechRecognizer.CreateOnDeviceSpeechRecognizer(_context)
                ?? throw new InvalidOperationException("Android devolvió un reconocedor local nulo.");
            _recognizer.SetRecognitionListener(this);
            _recognizerIntent = BuildRecognizerIntent(_mode);
            _activeSessionId = sessionId;
            _partialCommand = null;

            _setEvent($"Android S{sessionId} · iniciando {_mode}");
            _recognizer.StartListening(_recognizerIntent);
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"Android voice S{sessionId} no pudo iniciar: {ex}");
            CloseSession(sessionId, cancel: true);
            _setStatus($"Voz local no disponible · {ex.Message}");
            ScheduleListen(1_000, "reintento tras fallo");
        }
    }

    private Intent BuildRecognizerIntent(ListeningMode mode)
    {
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraLanguage, "es-ES");
        intent.PutExtra(RecognizerIntent.ExtraPreferOffline, true);
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 5);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputMinimumLengthMillis, 180L);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, 450L);
        intent.PutExtra(RecognizerIntent.ExtraSpeechInputPossiblyCompleteSilenceLengthMillis, 300L);

        if (mode == ListeningMode.WakeWord && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            BardoSettings settings = _settingsProvider();
            intent.PutStringArrayListExtra(
                RecognizerIntent.ExtraBiasingStrings,
                new List<string> { settings.WakeWord, "Bardo", "Vardo", "Pardo" });
        }

        return intent;
    }

    public void OnReadyForSpeech(Bundle? @params)
    {
        if (_activeSessionId is not long sessionId)
        {
            return;
        }

        _setEvent($"Android S{sessionId} · micrófono listo · {_mode}");
        if (_mode == ListeningMode.Command)
        {
            _setStatus("Bardo · habla ahora");
        }
    }

    public void OnBeginningOfSpeech()
    {
        if (_activeSessionId is not long sessionId)
        {
            return;
        }

        _setEvent($"Android S{sessionId} · voz detectada · {_mode}");
        if (_mode == ListeningMode.Command)
        {
            _setStatus("Escuchando comando…");
        }
    }

    public void OnRmsChanged(float rmsdB) => _setRms(rmsdB);
    public void OnBufferReceived(byte[]? buffer) { }

    public void OnEndOfSpeech()
    {
        if (_activeSessionId is not long sessionId)
        {
            return;
        }

        _setEvent($"Android S{sessionId} · fin de voz · {_mode}");
        if (_mode == ListeningMode.Command && !string.IsNullOrWhiteSpace(_partialCommand))
        {
            SchedulePartialCommand(sessionId, EndOfSpeechCommandDelayMilliseconds);
        }
    }

    public void OnError(SpeechRecognizerError error)
    {
        if (_activeSessionId is not long sessionId)
        {
            return;
        }

        ListeningMode failedMode = _mode;
        _setEvent($"Android S{sessionId} · error {error} · {failedMode}");
        Log.Debug(LogTag, $"Android voice S{sessionId} error={error} mode={failedMode}");

        if (failedMode == ListeningMode.Command && !string.IsNullOrWhiteSpace(_partialCommand))
        {
            DispatchCommand(sessionId, _partialCommand!);
            return;
        }

        CloseSession(sessionId, cancel: false);
        if (failedMode == ListeningMode.Command)
        {
            RetryOrReturnToWake(error is SpeechRecognizerError.NoMatch or SpeechRecognizerError.SpeechTimeout
                ? "No te he oído"
                : $"Error de voz: {error}");
            return;
        }

        ScheduleListen(error == SpeechRecognizerError.RecognizerBusy ? 800 : 120, $"error {error}");
    }

    public void OnResults(Bundle? results)
    {
        if (_activeSessionId is not long sessionId)
        {
            return;
        }

        IReadOnlyList<string> alternatives = ReadAlternatives(results);
        Log.Info(LogTag, $"Android voice S{sessionId} resultado {_mode}: {FormatAlternatives(alternatives)}");
        _setEvent($"Android S{sessionId} · {FormatAlternatives(alternatives)}");

        if (_mode == ListeningMode.WakeWord)
        {
            string? wakeMatch = FindWakeMatch(alternatives);
            if (wakeMatch is not null)
            {
                EnterCommandMode(sessionId, wakeMatch);
                return;
            }

            CloseSession(sessionId, cancel: false);
            ScheduleListen(80, "resultado sin Bardo");
            return;
        }

        string? command = alternatives.FirstOrDefault() ?? _partialCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            CloseSession(sessionId, cancel: false);
            RetryOrReturnToWake("No he entendido la orden");
            return;
        }

        DispatchCommand(sessionId, command);
    }

    public void OnPartialResults(Bundle? partialResults)
    {
        if (_activeSessionId is not long sessionId)
        {
            return;
        }

        IReadOnlyList<string> alternatives = ReadAlternatives(partialResults);
        if (alternatives.Count == 0)
        {
            return;
        }

        Log.Debug(LogTag, $"Android voice S{sessionId} parcial {_mode}: {FormatAlternatives(alternatives)}");
        _setEvent($"Android S{sessionId} parcial · {FormatAlternatives(alternatives)}");
        if (_mode == ListeningMode.WakeWord)
        {
            string? wakeMatch = FindWakeMatch(alternatives);
            if (wakeMatch is not null)
            {
                EnterCommandMode(sessionId, wakeMatch);
            }

            return;
        }

        _partialCommand = alternatives[0];
        SchedulePartialCommand(sessionId, PartialCommandDelayMilliseconds);
    }

    public void OnEvent(int eventType, Bundle? @params) { }
    public void OnSegmentResults(Bundle segmentResults) { }
    public void OnEndOfSegmentedSession() { }
    public void OnLanguageDetection(Bundle results) { }

    private string? FindWakeMatch(IEnumerable<string> alternatives)
    {
        BardoSettings settings = _settingsProvider();
        return alternatives.FirstOrDefault(value => WakeWordMatcher.Matches(value, settings.WakeWord));
    }

    private void EnterCommandMode(long sessionId, string wakeText)
    {
        if (_activeSessionId != sessionId || _mode != ListeningMode.WakeWord)
        {
            return;
        }

        BardoSettings settings = _settingsProvider();
        string inlineCommand = WakeWordMatcher.ExtractCommandAfterWakeWord(
            wakeText,
            settings.WakeWord);

        Log.Info(LogTag, $"Wake Android detectada: {wakeText}");
        CloseSession(sessionId, cancel: true);
        _playWakeAcknowledgement();

        if (!string.IsNullOrWhiteSpace(inlineCommand))
        {
            _mode = ListeningMode.Command;
            DispatchInlineCommand(inlineCommand);
            return;
        }

        _mode = ListeningMode.Command;
        _commandRetryCount = 0;
        _partialCommand = null;
        _setStatus("Bardo detectado · preparando micrófono…");
        // El tono dura 180 ms. Empezar la sesión después evita que el propio
        // pitido contamine el principio de la orden, sin añadir una pausa visible.
        ScheduleListen(230, "transición a comando tras tono");
    }

    private void SchedulePartialCommand(long sessionId, long delayMilliseconds)
    {
        _handler.RemoveCallbacksAndMessages(null);
        _handler.PostDelayed(() =>
        {
            if (_activeSessionId == sessionId &&
                _mode == ListeningMode.Command &&
                !string.IsNullOrWhiteSpace(_partialCommand))
            {
                DispatchCommand(sessionId, _partialCommand!);
            }
        }, delayMilliseconds);
    }

    private void DispatchCommand(long sessionId, string command)
    {
        if (_activeSessionId != sessionId || _mode != ListeningMode.Command)
        {
            return;
        }

        BardoSettings settings = _settingsProvider();
        if (WakeWordMatcher.Matches(command, settings.WakeWord) &&
            command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2)
        {
            CloseSession(sessionId, cancel: true);
            RetryOrReturnToWake("Te escucho · di la orden");
            return;
        }

        CloseSession(sessionId, cancel: true);
        DispatchInlineCommand(command);
    }

    private void DispatchInlineCommand(string command)
    {
        Log.Info(LogTag, $"Comando Android reconocido: {command.Trim()}");
        _commandInFlight = true;
        _partialCommand = null;
        _setStatus($"Ejecutando: {command.Trim()}");
        _ = ExecuteAndReturnToWakeAsync(command.Trim());
    }

    private async Task ExecuteAndReturnToWakeAsync(string command)
    {
        try
        {
            await _executeCommand(command, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, $"Fallo ejecutando comando reconocido: {ex}");
            _setStatus($"Error · {ex.Message}");
        }
        finally
        {
            _handler.Post(ReturnToWakeWord);
        }
    }

    private void RetryOrReturnToWake(string reason)
    {
        if (_commandRetryCount == 0)
        {
            _commandRetryCount++;
            _mode = ListeningMode.Command;
            _setStatus($"{reason} · habla ahora");
            ScheduleListen(120, "segundo intento de comando");
            return;
        }

        _setStatus($"{reason} · volviendo a esperar Bardo");
        _handler.PostDelayed(ReturnToWakeWord, 350);
    }

    private void ReturnToWakeWord()
    {
        if (_disposed)
        {
            return;
        }

        if (_activeSessionId is long sessionId)
        {
            CloseSession(sessionId, cancel: true);
        }

        _commandInFlight = false;
        _partialCommand = null;
        _commandRetryCount = 0;
        _mode = ListeningMode.WakeWord;
        SetWaitingStatus();
        ScheduleListen(80, "retorno a wake word");
    }

    private void SetWaitingStatus()
    {
        BardoSettings settings = _settingsProvider();
        _setStatus($"Esperando «{settings.WakeWord}» · voz local de Android");
    }

    private void CloseSession(long sessionId, bool cancel)
    {
        if (_activeSessionId != sessionId)
        {
            return;
        }

        _activeSessionId = null;
        _handler.RemoveCallbacksAndMessages(null);
        SpeechRecognizer? recognizer = _recognizer;
        _recognizer = null;
        _recognizerIntent?.Dispose();
        _recognizerIntent = null;

        if (recognizer is null)
        {
            return;
        }

        if (cancel)
        {
            try
            {
                recognizer.Cancel();
            }
            catch
            {
            }
        }

        try
        {
            recognizer.Destroy();
        }
        catch
        {
        }

        recognizer.Dispose();
    }

    private static IReadOnlyList<string> ReadAlternatives(Bundle? results)
    {
        var values = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
        return values is { Count: > 0 }
            ? values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray()
            : [];
    }

    private static string FormatAlternatives(IReadOnlyList<string> alternatives) =>
        alternatives.Count == 0 ? "<vacío>" : string.Join(" | ", alternatives);

    public new void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _handler.RemoveCallbacksAndMessages(null);
        if (_activeSessionId is long sessionId)
        {
            CloseSession(sessionId, cancel: true);
        }

        _lifetime.Dispose();
        _handler.Dispose();
    }

    private enum ListeningMode
    {
        WakeWord,
        Command
    }
}
