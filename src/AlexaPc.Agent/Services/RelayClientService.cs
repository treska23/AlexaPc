using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class RelayClientService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RelayConfigurationService _configurationService;
    private readonly CommandDispatcher _dispatcher;
    private readonly AppLogService _log;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private string? _lastConnectionLabel;

    public RelayClientService(
        RelayConfigurationService configurationService,
        CommandDispatcher dispatcher,
        AppLogService log)
    {
        _configurationService = configurationService;
        _dispatcher = dispatcher;
        _log = log;
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
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);

                var uri = BuildUri(settings.RelayUrl, settings.DeviceId, settings.DeviceToken);
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

                Publish(true, "RELAY · CONECTADO");
                await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);

                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("relay_connection_failed", ex);
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
        var activeCommands = new HashSet<Task>();

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log.Info("relay_connection_closed", new
                        {
                            closeStatus = result.CloseStatus?.ToString(),
                            reason = result.CloseStatusDescription
                        });

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
                RelayCommandMessage? request;
                try
                {
                    request = JsonSerializer.Deserialize<RelayCommandMessage>(json, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _log.Error("relay_message_invalid", ex);
                    continue;
                }

                if (request is null ||
                    !string.Equals(request.Type, "execute", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(request.RequestId))
                {
                    continue;
                }

                var commandTask = ProcessCommandAsync(socket, request, cancellationToken);
                lock (activeCommands)
                {
                    activeCommands.Add(commandTask);
                }

                _ = commandTask.ContinueWith(
                    completedTask =>
                    {
                        lock (activeCommands)
                        {
                            activeCommands.Remove(completedTask);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            Task[] remaining;
            lock (activeCommands)
            {
                remaining = activeCommands.ToArray();
            }

            if (remaining.Length > 0)
            {
                try
                {
                    await Task.WhenAll(remaining)
                        .WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Cada tarea registra su resultado; aquí solo evitamos bloquear la reconexión.
                }
            }
        }
    }

    private async Task ProcessCommandAsync(
        ClientWebSocket socket,
        RelayCommandMessage request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _log.Info("remote_command_received", new
        {
            requestId = request.RequestId,
            command = request.Command
        });

        CommandResult commandResult;
        try
        {
            commandResult = string.IsNullOrWhiteSpace(request.Command)
                ? CommandResult.Fail("La orden recibida está vacía.")
                : await _dispatcher
                    .ExecuteByNameAsync(request.Command, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _log.Error("remote_command_cancelled", ex, new { requestId = request.RequestId });
            commandResult = CommandResult.Fail("La orden se canceló antes de terminar.");
        }
        catch (Exception ex)
        {
            _log.Error("remote_command_failed", ex, new { requestId = request.RequestId });
            commandResult = CommandResult.Fail("El agente no pudo completar la orden.");
        }

        _log.Info("remote_command_completed", new
        {
            requestId = request.RequestId,
            command = request.Command,
            success = commandResult.Success,
            durationMs = stopwatch.ElapsedMilliseconds
        });

        await SendResultAsync(socket, request.RequestId, commandResult).ConfigureAwait(false);
    }

    private async Task SendResultAsync(
        ClientWebSocket socket,
        string requestId,
        CommandResult commandResult)
    {
        var response = new RelayResultMessage(
            "result",
            requestId,
            commandResult.Success,
            commandResult.Message);

        var responseJson = JsonSerializer.Serialize(response, JsonOptions);
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);
        using var sendTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var gateAcquired = false;

        try
        {
            await _sendGate.WaitAsync(sendTimeout.Token).ConfigureAwait(false);
            gateAcquired = true;

            if (socket.State != WebSocketState.Open)
            {
                throw new WebSocketException("La conexión se cerró antes de enviar el resultado.");
            }

            await socket.SendAsync(
                new ArraySegment<byte>(responseBytes),
                WebSocketMessageType.Text,
                true,
                sendTimeout.Token).ConfigureAwait(false);

            _log.Info("remote_result_sent", new
            {
                requestId,
                success = commandResult.Success
            });
        }
        catch (Exception ex)
        {
            _log.Error("remote_result_send_failed", ex, new { requestId });
        }
        finally
        {
            if (gateAcquired)
            {
                _sendGate.Release();
            }
        }
    }

    private static Uri BuildUri(string relayUrl, string deviceId, string token)
    {
        var separator = relayUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return new Uri(
            $"{relayUrl}{separator}deviceId={Uri.EscapeDataString(deviceId)}&token={Uri.EscapeDataString(token)}");
    }

    private void Publish(bool isConnected, string label)
    {
        if (!string.Equals(_lastConnectionLabel, label, StringComparison.Ordinal))
        {
            _lastConnectionLabel = label;
            _log.Info("relay_state_changed", new { isConnected, label });
        }

        ConnectionStateChanged?.Invoke(this, new RelayConnectionStateChangedEventArgs(isConnected, label));
    }

    private sealed record RelayCommandMessage(string Type, string RequestId, string Command);
    private sealed record RelayResultMessage(string Type, string RequestId, bool Success, string Message);
}

public sealed record RelayConnectionStateChangedEventArgs(bool IsConnected, string Label);
