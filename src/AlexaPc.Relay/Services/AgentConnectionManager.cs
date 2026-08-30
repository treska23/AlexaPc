using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlexaPc.Relay.Contracts;

namespace AlexaPc.Relay.Services;

public sealed class AgentConnectionManager
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public int ConnectedAgents => _sessions.Count;

    public async Task RegisterAndListenAsync(
        string deviceId,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var session = new AgentSession(deviceId, socket);

        if (_sessions.TryGetValue(deviceId, out var previous))
        {
            await previous.CloseAsync().ConfigureAwait(false);
        }

        _sessions[deviceId] = session;

        try
        {
            await session.ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_sessions.TryGetValue(deviceId, out var current) && ReferenceEquals(current, session))
            {
                _sessions.TryRemove(deviceId, out _);
            }

            session.FailPending();
        }
    }

    public Task<CommandApiResponse?> ExecuteAsync(
        string deviceId,
        string command,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(deviceId, out var session) || session.SocketState != WebSocketState.Open)
        {
            return Task.FromResult<CommandApiResponse?>(null);
        }

        return ExecuteCoreAsync(session, command, cancellationToken);
    }

    private static async Task<CommandApiResponse?> ExecuteCoreAsync(
        AgentSession session,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await session.ExecuteAsync(
                command,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new CommandApiResponse(false, "El ordenador no respondió a tiempo.");
        }
        catch (Exception ex)
        {
            return new CommandApiResponse(false, $"Error de comunicación con el ordenador: {ex.Message}");
        }
    }

    private sealed class AgentSession
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayResultMessage>> _pending = new();

        public AgentSession(string deviceId, WebSocket socket)
        {
            DeviceId = deviceId;
            _socket = socket;
        }

        public string DeviceId { get; }
        public WebSocketState SocketState => _socket.State;

        public async Task<CommandApiResponse> ExecuteAsync(
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<RelayResultMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_pending.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException("No se pudo registrar la orden remota.");
            }

            try
            {
                var message = new RelayCommandMessage("execute", requestId, command);
                var json = JsonSerializer.Serialize(message, JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);

                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }

                var result = await completion.Task
                    .WaitAsync(timeout, cancellationToken)
                    .ConfigureAwait(false);

                return new CommandApiResponse(result.Success, result.Message);
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
        }

        public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var json = await ReadTextMessageAsync(_socket, cancellationToken).ConfigureAwait(false);
                if (json is null)
                {
                    return;
                }

                RelayResultMessage? result;
                try
                {
                    result = JsonSerializer.Deserialize<RelayResultMessage>(json, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (result is null || !string.Equals(result.Type, "result", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_pending.TryRemove(result.RequestId, out var completion))
                {
                    completion.TrySetResult(result);
                }
            }
        }

        public async Task CloseAsync()
        {
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Replaced by a newer connection",
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }

        public void FailPending()
        {
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var completion))
                {
                    completion.TrySetException(new IOException("El agente se ha desconectado."));
                }
            }
        }

        private static async Task<string?> ReadTextMessageAsync(
            WebSocket socket,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var segment = new ArraySegment<byte>(buffer);
            using var stream = new MemoryStream();

            while (true)
            {
                var result = await socket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    if (result.EndOfMessage)
                    {
                        return string.Empty;
                    }

                    continue;
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }
    }
}
