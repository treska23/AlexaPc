using System.Net.Http.Json;
using System.Text.Json;

namespace Bardo.Mobile.Infrastructure;

internal sealed class RelayCommandClient
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RelayCommandResult> SendAsync(
        BardoSettings settings,
        string command,
        CancellationToken cancellationToken = default)
    {
        var endpoint = $"{settings.RelayUrl.TrimEnd('/')}/api/commands";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new RelayCommandRequest(settings.DeviceId, command))
        };

        request.Headers.TryAddWithoutValidation("X-AlexaPc-Api-Key", settings.ApiKey);

        try
        {
            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new RelayCommandResult(false, $"Relay HTTP {(int)response.StatusCode}: {payload}");
            }

            var result = JsonSerializer.Deserialize<RelayCommandResponse>(payload, JsonOptions);
            return result is null
                ? new RelayCommandResult(false, "El relay devolvió una respuesta vacía.")
                : new RelayCommandResult(result.Success, result.Message);
        }
        catch (Exception ex)
        {
            return new RelayCommandResult(false, ex.Message);
        }
    }

    private sealed record RelayCommandRequest(string DeviceId, string Command);
    private sealed record RelayCommandResponse(bool Success, string Message);
}

internal sealed record RelayCommandResult(bool Success, string Message);
