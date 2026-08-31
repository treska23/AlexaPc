using AlexaPc.Agent.Models;
using ControlPCIA;

namespace AlexaPc.Agent.Services;

public sealed class GeneralComputerControlService
{
    private const string SpokenPrefix = "[bardo] ";
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromMilliseconds(4800);

    private readonly AppLogService _log;

    public GeneralComputerControlService(AppLogService log)
    {
        _log = log;
    }

    public async Task<CommandResult?> TryHandleAsync(
        string utterance,
        CancellationToken cancellationToken = default)
    {
        if (IsPowerCommand(utterance))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExecutionTimeout);
        var startedAt = Environment.TickCount64;
        _log.Info("general_control_started", new { utterance });

        try
        {
            var result = await GeneralComputerController
                .ExecuteAsync(utterance, timeout.Token)
                .ConfigureAwait(false);

            _log.Info("general_control_completed", new
            {
                completed = result.Completed,
                state = result.State,
                stepCount = result.StepCount,
                durationMs = Environment.TickCount64 - startedAt
            });

            var message = NormalizeSpokenMessage(result.Message);
            return message.Length > 0
                ? CommandResult.Ok(SpokenPrefix + message)
                : CommandResult.Fail("El controlador general no devolvió una respuesta.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Info("general_control_timed_out", new
            {
                durationMs = Environment.TickCount64 - startedAt
            });
            return CommandResult.Fail("El controlador del ordenador no respondió a tiempo.");
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("La orden se canceló antes de terminar.");
        }
        catch (Exception ex)
        {
            _log.Error("general_control_failed", ex, new
            {
                durationMs = Environment.TickCount64 - startedAt
            });
            return CommandResult.Fail("El controlador general no pudo completar la orden.");
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await GeneralComputerController.WarmUpAsync(cancellationToken).ConfigureAwait(false);
            _log.Info("general_control_warmup_completed");
        }
        catch (OperationCanceledException)
        {
            _log.Info("general_control_warmup_cancelled");
        }
        catch (Exception ex)
        {
            _log.Error("general_control_warmup_failed", ex);
        }
    }

    private static bool IsPowerCommand(string utterance)
    {
        var normalized = utterance.ToLowerInvariant();
        return normalized.Contains("apag", StringComparison.Ordinal)
               || normalized.Contains("reinici", StringComparison.Ordinal)
               || normalized.Contains("suspend", StringComparison.Ordinal)
               || normalized.Contains("bloque", StringComparison.Ordinal);
    }

    private static string NormalizeSpokenMessage(string message)
    {
        var normalized = string.Join(
            " ",
            message
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.TrimStart('-', '•').Trim())
                .Where(line => line.Length > 0));

        const int maximumSpokenLength = 700;
        return normalized.Length <= maximumSpokenLength
            ? normalized
            : normalized[..maximumSpokenLength].TrimEnd() + "…";
    }
}
