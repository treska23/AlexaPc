using Android.Content;
using Android.Media;
using SherpaOnnx;
using System.Diagnostics;

namespace Bardo.Mobile.Infrastructure;

internal sealed record LocalSpeechUtterance(
    string Text,
    float PeakRmsDb,
    TimeSpan Duration);

/// <summary>
/// Captura audio directamente con AudioRecord y lo transcribe en el propio
/// teléfono con Moonshine ES y Whisper mediante sherpa-onnx. No usa
/// SpeechRecognizer ni necesita Google/Internet una vez descargados los modelos.
/// </summary>
internal sealed class LocalSpeechEngine : IDisposable
{
    private const int SampleRate = 16_000;
    private const int FrameSamples = 320; // 20 ms
    private const int PreRollFrames = 20; // 400 ms: conserva consonantes/sílabas iniciales al hablar rápido.
    private const int StartFramesRequired = 2;
    private const int FastEndSilenceFrames = 14; // 280 ms tras órdenes cortas.
    private const int NormalEndSilenceFrames = 18; // 360 ms tras frases normales.
    private const int LongEndSilenceFrames = 24; // 480 ms tras órdenes largas.
    private const int MinimumSpeechFrames = 5; // 100 ms

    private readonly Context _context;
    private readonly AudioRecord _audioRecord;
    private readonly OfflineRecognizer _recognizer;
    private OfflineRecognizer? _improvedCommandRecognizer;
    private bool _disposed;
    private float _noiseFloor = 0.006f;

    private LocalSpeechEngine(
        Context context,
        AudioRecord audioRecord,
        OfflineRecognizer recognizer)
    {
        _context = context.ApplicationContext ?? context;
        _audioRecord = audioRecord;
        _recognizer = recognizer;
    }

    public bool ImprovedCommandRecognitionReady => _improvedCommandRecognizer is not null;

    public static async Task<LocalSpeechEngine> CreateAsync(
        Context context,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LocalSpeechModelPaths model = await LocalSpeechModelManager.EnsureInstalledAsync(
            context,
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Cargando motor español local…");

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Moonshine.Encoder = model.Encoder;
        config.ModelConfig.Moonshine.MergedDecoder = model.Decoder;
        config.ModelConfig.Tokens = model.Tokens;
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        var recognizer = new OfflineRecognizer(config);

        int minimumBuffer = AudioRecord.GetMinBufferSize(
            SampleRate,
            ChannelIn.Mono,
            Android.Media.Encoding.Pcm16bit);
        int bufferBytes = Math.Max(minimumBuffer, FrameSamples * sizeof(short) * 8);

        var recorder = new AudioRecord(
            AudioSource.VoiceRecognition,
            SampleRate,
            ChannelIn.Mono,
            Android.Media.Encoding.Pcm16bit,
            bufferBytes);

        if (recorder.State != State.Initialized)
        {
            recorder.Dispose();
            recognizer.Dispose();
            throw new InvalidOperationException("Android no pudo inicializar AudioRecord a 16 kHz.");
        }

        progress?.Invoke("Motor español local listo");
        return new LocalSpeechEngine(context, recorder, recognizer);
    }

    public async Task PrepareImprovedCommandRecognitionAsync(
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        WhisperSpeechModelPaths model = await WhisperSpeechModelManager.EnsureInstalledAsync(
            _context,
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Cargando Whisper español…");
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Whisper.Encoder = model.Encoder;
        config.ModelConfig.Whisper.Decoder = model.Decoder;
        config.ModelConfig.Whisper.Language = "es";
        config.ModelConfig.Whisper.Task = "transcribe";
        config.ModelConfig.Whisper.TailPaddings = -1;
        config.ModelConfig.Tokens = model.Tokens;
        config.ModelConfig.NumThreads = 4;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        var recognizer = new OfflineRecognizer(config);
        ValidateRecognizer(recognizer);
        if (_disposed)
        {
            recognizer.Dispose();
            throw new ObjectDisposedException(nameof(LocalSpeechEngine));
        }

        OfflineRecognizer? previous = Interlocked.Exchange(
            ref _improvedCommandRecognizer,
            recognizer);
        previous?.Dispose();
        progress?.Invoke("Whisper español listo");
    }

    private static void ValidateRecognizer(OfflineRecognizer recognizer)
    {
        // Fuerza una inferencia corta para detectar aquí modelos incompatibles o
        // descargas corruptas, antes de anunciar que Whisper está disponible.
        using OfflineStream stream = recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, new float[SampleRate / 2]);
        recognizer.Decode(stream);
        _ = stream.Result.Text;
    }

    public async Task<LocalSpeechUtterance?> ListenForUtteranceAsync(
        TimeSpan? timeout,
        TimeSpan maximumUtterance,
        Action<float>? rmsChanged,
        Action<string>? eventChanged,
        CancellationToken cancellationToken,
        bool preferImprovedRecognizer = false)
    {
        ThrowIfDisposed();
        EnsureRecording();

        var overall = Stopwatch.StartNew();
        var spoken = Stopwatch.StartNew();
        var frame = new short[FrameSamples];
        var preRoll = new Queue<short[]>(PreRollFrames);
        var samples = new List<short>(SampleRate * (int)Math.Ceiling(maximumUtterance.TotalSeconds));
        bool inSpeech = false;
        int loudFrames = 0;
        int silentFrames = 0;
        int speechFrames = 0;
        float peakDb = -120f;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (timeout is not null && !inSpeech && overall.Elapsed >= timeout.Value)
                {
                    eventChanged?.Invoke("tiempo de escucha agotado");
                    return null;
                }

                int read = _audioRecord.Read(frame, 0, frame.Length);
                if (read <= 0)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                float rms = CalculateRms(frame, read);
                float rmsDb = LinearToDb(rms);
                peakDb = Math.Max(peakDb, rmsDb);
                rmsChanged?.Invoke(rmsDb);

                // Umbral de arranque tolerante para no comernos la primera sílaba.
                float startThreshold = Math.Max(0.0052f, _noiseFloor * 1.75f);

                // Para terminar una frase usamos histéresis: el umbral es bastante más
                // alto que el ruido base. Así la TV o el ruido de habitación no alargan
                // artificialmente la escucha después de la última palabra.
                float silenceThreshold = Math.Max(0.0045f, _noiseFloor * 1.45f);

                if (!inSpeech)
                {
                    // Seguimos el ruido ambiente lentamente para que el teléfono funcione
                    // igual de noche, con TV encendida o desde otra zona de la habitación.
                    if (rms < startThreshold)
                    {
                        _noiseFloor = Math.Clamp((_noiseFloor * 0.985f) + (rms * 0.015f), 0.0015f, 0.08f);
                    }

                    EnqueuePreRoll(preRoll, frame, read);

                    if (rms >= startThreshold)
                    {
                        loudFrames++;
                    }
                    else
                    {
                        loudFrames = 0;
                    }

                    if (loudFrames < StartFramesRequired)
                    {
                        continue;
                    }

                    inSpeech = true;
                    spoken.Restart();
                    eventChanged?.Invoke("voz detectada por AudioRecord");
                    foreach (short[] previousFrame in preRoll)
                    {
                        samples.AddRange(previousFrame);
                    }
                    preRoll.Clear();
                }

                samples.AddRange(frame.AsSpan(0, read).ToArray());
                speechFrames++;

                if (rms <= silenceThreshold)
                {
                    silentFrames++;
                }
                else
                {
                    silentFrames = 0;
                }

                bool enoughSpeech = speechFrames >= MinimumSpeechFrames;
                int requiredSilenceFrames = GetRequiredEndSilenceFrames(spoken.Elapsed);
                bool endedBySilence = enoughSpeech && silentFrames >= requiredSilenceFrames;
                bool reachedMaximum = spoken.Elapsed >= maximumUtterance;

                if (endedBySilence)
                {
                    eventChanged?.Invoke(
                        $"fin de frase detectado · {requiredSilenceFrames * 20} ms desde la última voz");
                }

                if (endedBySilence || reachedMaximum)
                {
                    break;
                }
            }
        }
        finally
        {
            StopRecording();
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (speechFrames < MinimumSpeechFrames || samples.Count < SampleRate / 8)
        {
            eventChanged?.Invoke("fragmento demasiado corto");
            return null;
        }

        eventChanged?.Invoke("transcribiendo localmente");
        OfflineRecognizer decoder = preferImprovedRecognizer && _improvedCommandRecognizer is not null
            ? _improvedCommandRecognizer
            : _recognizer;
        string text = await DecodeAsync(samples, decoder, cancellationToken).ConfigureAwait(false);
        eventChanged?.Invoke(string.IsNullOrWhiteSpace(text)
            ? "transcripción local vacía"
            : $"local: {text}");

        return string.IsNullOrWhiteSpace(text)
            ? null
            : new LocalSpeechUtterance(text.Trim(), peakDb, spoken.Elapsed);
    }

    private static int GetRequiredEndSilenceFrames(TimeSpan spokenDuration)
    {
        // El final ya no usa una espera fija de 720 ms. Se decide por silencio
        // consecutivo desde la última voz y se adapta a la longitud de la frase.
        // Una orden tipo «play en la tele» queda lista en ~280 ms tras terminar.
        if (spokenDuration < TimeSpan.FromSeconds(1.5))
        {
            return FastEndSilenceFrames;
        }

        if (spokenDuration < TimeSpan.FromSeconds(3))
        {
            return NormalEndSilenceFrames;
        }

        return LongEndSilenceFrames;
    }

    private async Task<string> DecodeAsync(
        IReadOnlyList<short> pcm,
        OfflineRecognizer recognizer,
        CancellationToken cancellationToken)
    {
        float[] samples = new float[pcm.Count];
        for (int i = 0; i < pcm.Count; i++)
        {
            samples[i] = pcm[i] / 32768f;
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using OfflineStream stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, samples);
            recognizer.Decode(stream);
            return stream.Result.Text ?? string.Empty;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureRecording()
    {
        if (_audioRecord.RecordingState == RecordState.Recording)
        {
            return;
        }

        _audioRecord.StartRecording();
        if (_audioRecord.RecordingState != RecordState.Recording)
        {
            throw new InvalidOperationException("El micrófono local no ha empezado a grabar.");
        }
    }

    private void StopRecording()
    {
        try
        {
            if (_audioRecord.RecordingState == RecordState.Recording)
            {
                _audioRecord.Stop();
            }
        }
        catch
        {
            // Dispose hará una segunda limpieza si Android estaba cerrando el servicio.
        }
    }

    private static void EnqueuePreRoll(Queue<short[]> queue, short[] source, int count)
    {
        queue.Enqueue(source.AsSpan(0, count).ToArray());
        while (queue.Count > PreRollFrames)
        {
            queue.Dequeue();
        }
    }

    private static float CalculateRms(short[] samples, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            double value = samples[i] / 32768.0;
            sum += value * value;
        }

        return count == 0 ? 0f : (float)Math.Sqrt(sum / count);
    }

    private static float LinearToDb(float value) =>
        20f * MathF.Log10(MathF.Max(value, 0.000001f));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRecording();
        try
        {
            _audioRecord.Release();
        }
        catch
        {
            // Android puede haber liberado ya el recurso durante el cierre.
        }
        _audioRecord.Dispose();
        Interlocked.Exchange(ref _improvedCommandRecognizer, null)?.Dispose();
        _recognizer.Dispose();
    }
}
