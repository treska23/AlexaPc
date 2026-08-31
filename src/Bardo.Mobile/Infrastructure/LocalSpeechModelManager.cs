using Android.Content;

namespace Bardo.Mobile.Infrastructure;

internal sealed record LocalSpeechModelPaths(
    string Encoder,
    string Decoder,
    string Tokens);

internal static class LocalSpeechModelManager
{
    private const string ModelDirectoryName = "moonshine-base-es-2026-02-27";
    private const string BaseUrl =
        "https://huggingface.co/csukuangfj2/sherpa-onnx-moonshine-base-es-quantized-2026-02-27/resolve/main";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static bool IsInstalled(Context context)
    {
        LocalSpeechModelPaths paths = GetPaths(context);
        return IsUsable(paths.Encoder, 5_000_000) &&
               IsUsable(paths.Decoder, 10_000_000) &&
               IsUsable(paths.Tokens, 10_000);
    }

    public static async Task<LocalSpeechModelPaths> EnsureInstalledAsync(
        Context context,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LocalSpeechModelPaths paths = GetPaths(context);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Encoder)!);

        await EnsureFileAsync(
            "encoder_model.ort",
            paths.Encoder,
            5_000_000,
            progress,
            cancellationToken).ConfigureAwait(false);

        await EnsureFileAsync(
            "decoder_model_merged.ort",
            paths.Decoder,
            10_000_000,
            progress,
            cancellationToken).ConfigureAwait(false);

        await EnsureFileAsync(
            "tokens.txt",
            paths.Tokens,
            10_000,
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Modelo español local preparado");
        return paths;
    }

    private static LocalSpeechModelPaths GetPaths(Context context)
    {
        string root = Path.Combine(context.FilesDir!.AbsolutePath, "models", ModelDirectoryName);
        return new LocalSpeechModelPaths(
            Path.Combine(root, "encoder_model.ort"),
            Path.Combine(root, "decoder_model_merged.ort"),
            Path.Combine(root, "tokens.txt"));
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

            progress?.Invoke($"Descargando voz local · {remoteName}");
            using HttpResponseMessage response = await HttpClient.GetAsync(
                $"{BaseUrl}/{remoteName}?download=true",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
                        progress?.Invoke($"Descargando voz local · {remoteName} · {percent}%");
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
                // No ocultar el error original por un fallo limpiando un temporal.
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
