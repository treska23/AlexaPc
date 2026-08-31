using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace AlexaPc.Agent.Services;

public sealed class LocalRelayProcessService : IAsyncDisposable
{
    private static readonly Uri HealthUri = new("http://127.0.0.1:5184/health");

    private readonly AppLogService _log;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private Process? _ownedProcess;

    public LocalRelayProcessService(AppLogService log)
    {
        _log = log;
    }

    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _lifetime = new CancellationTokenSource();
        _worker = RunAsync(_lifetime.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    EnsureStarted();
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("local_relay_monitor_failed", ex);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync(HealthUri, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureStarted()
    {
        if (_ownedProcess is { HasExited: false })
        {
            return;
        }

        _ownedProcess?.Dispose();
        _ownedProcess = null;

        string relayExecutable = Path.Combine(
            AppContext.BaseDirectory,
            "Relay",
            "AlexaPc.Relay.exe");
        if (!File.Exists(relayExecutable))
        {
            throw new FileNotFoundException(
                "No se encuentra el relay incluido con AlexaPc.",
                relayExecutable);
        }

        _ownedProcess = Process.Start(new ProcessStartInfo
        {
            FileName = relayExecutable,
            WorkingDirectory = Path.GetDirectoryName(relayExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Windows no pudo iniciar AlexaPc.Relay.");

        _log.Info("local_relay_started", new
        {
            processId = _ownedProcess.Id,
            executable = relayExecutable
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null)
        {
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

        if (_ownedProcess is not null)
        {
            try
            {
                if (!_ownedProcess.HasExited)
                {
                    _ownedProcess.Kill(entireProcessTree: true);
                    await _ownedProcess.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch
            {
            }

            _ownedProcess.Dispose();
            _ownedProcess = null;
        }

        _httpClient.Dispose();
    }
}
