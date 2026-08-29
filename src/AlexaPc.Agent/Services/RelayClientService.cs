using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AlexaPc.Agent.Services;

public sealed class RelayClientService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RelayConfigurationService _configurationService;
    private readonly CommandDispatcher _dispatcher;
    private CancellationTokenSource? _lifetime;
    private Task? _worker;

    public RelayClientService(
        RelayConfigurationService configurationService,
        CommandDispatcher dispatcher)
    {
        _configurationService = configurationService;
        _dispatcher = dispatcher;
    }

    public event EventHandler<RelayConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _lifetime = new CancellationTokenSource();
        _worker = RunAsync(_lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is null)
        {
            return;
        }

        _lifetime.Cancel();

        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
        _lifetime = null;
        _worker = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var settings = _configurationService.Load();
                if (!settings.Enabled)
                {
                    Publish(false, "RELAY · DESACTIVADO");
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                Publish(false, "RELAY · CONECTANDO");

                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                var uri = BuildUri(settings.RelayUrl, settings.DeviceId, settings.DeviceToken);
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

                Publish(true, "RELAY · CONECTADO");
                await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                Publish(false, "RELAY · REINTENTANDO");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }

        Publish(false, "RELAY · DESCONECTADO");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var segment = new ArraySegment<byte>(buffer);

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(stream.ToArray());
            var request = JsonSerializer.Deserialize<RelayCommandMessage>(json, JsonOptions);

            if (request is null || !string.Equals(request.Type, "execute", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var commandResult = await _dispatcher
                .ExecuteByNameAsync(request.Command, cancellationToken)
                .ConfigureAwait(false);

            var response = new RelayResultMessage(
                "result",
                request.RequestId,
                commandResult.Success,
                commandResult.Message);

            var responseJson = JsonSerializer.Serialize(response, JsonOptions);
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);

            await socket.SendAsync(
                new ArraySegment<byte>(responseBytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static Uri BuildUri(string relayUrl, string deviceId, string token)
    {
        var separator = relayUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return new Uri(
            $"{relayUrl}{separator}deviceId={Uri.EscapeDataString(deviceId)}&token={Uri.EscapeDataString(token)}");
    }

    private void Publish(bool isConnected, string label)
        => ConnectionStateChanged?.Invoke(this, new RelayConnectionStateChangedEventArgs(isConnected, label));

    private sealed record RelayCommandMessage(string Type, string RequestId, string Command);
    private sealed record RelayResultMessage(string Type, string RequestId, bool Success, string Message);
}

public sealed record RelayConnectionStateChangedEventArgs(bool IsConnected, string Label);
