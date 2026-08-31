using Android.Content;

namespace Bardo.Mobile.Infrastructure;

internal sealed record WhisperSpeechModelPaths(
    string Encoder,
    string Decoder,
    string Tokens);

internal static class WhisperSpeechModelManager
{
    private const string ModelDirectoryName = "whisper-base-multilingual-int8";
    private const string BaseUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-whisper-base/resolve/main";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<WhisperSpeechModelPaths> EnsureInstalledAsync(
        Context context,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        WhisperSpeechModelPaths paths = GetPaths(context);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Encoder)!);

        await EnsureFileAsync(
            "base-encoder.int8.onnx",
            paths.Encoder,
            20_000_000,
            progress,
            cancellationToken).ConfigureAwait(false);
        await EnsureFileAsync(
            "base-decoder.int8.onnx",
            paths.Decoder,
            100_000_000,
            progress,
            cancellationToken).ConfigureAwait(false);
        await EnsureFileAsync(
            "base-tokens.txt",
            paths.Tokens,
            500_000,
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Whisper español preparado");
        return paths;
    }

    private static WhisperSpeechModelPaths GetPaths(Context context)
    {
        string root = Path.Combine(context.FilesDir!.AbsolutePath, "models", ModelDirectoryName);
        return new WhisperSpeechModelPaths(
            Path.Combine(root, "base-encoder.int8.onnx"),
            Path.Combine(root, "base-decoder.int8.onnx"),
            Path.Combine(root, "base-tokens.txt"));
    }

    private static async Task EnsureFileAsync(
        string remoteName,
        string destination,
        long minimumLength,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (IsUsable(destination, minimumLength))
        {
            return;
        }

        string temporary = destination + ".download";
        try
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            progress?.Invoke($"Descargando Whisper · {remoteName}");
            using HttpResponseMessage response = await HttpClient.GetAsync(
                $"{BaseUrl}/{remoteName}?download=true",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                useAsync: true);

            var buffer = new byte[128 * 1024];
            long received = 0;
            int lastPercent = -1;

            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                if (total is > 0)
                {
                    int percent = (int)Math.Clamp(received * 100L / total.Value, 0, 100);
                    if (percent >= lastPercent + 10)
                    {
                        lastPercent = percent;
                        progress?.Invoke($"Descargando Whisper · {remoteName} · {percent}%");
                    }
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (!IsUsable(temporary, minimumLength))
            {
                throw new InvalidDataException($"La descarga de {remoteName} está incompleta.");
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
                // No ocultar el error que causó el fallo de descarga.
            }

            throw;
        }
    }

    private static bool IsUsable(string path, long minimumLength)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= minimumLength;
        }
        catch
        {
            return false;
        }
    }
}
