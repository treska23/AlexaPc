using System.Diagnostics;
using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class CommandExecutionService
{
    private readonly BuiltInActionService _builtInActionService = new();
    private readonly AppLogService _log;

    public CommandExecutionService(AppLogService log)
    {
        _log = log;
    }

    public async Task<CommandResult> ExecuteAsync(CommandDefinition command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        _log.Info("tool_execution_started", new
        {
            command = command.Name,
            type = command.Type.ToString(),
            target = command.Target
        });

        try
        {
            var result = command.Type switch
            {
                CommandType.Process => ExecuteProcess(command),
                CommandType.Url => OpenUrl(command),
                CommandType.BuiltIn => await _builtInActionService.ExecuteAsync(command.Target, cancellationToken),
                _ => CommandResult.Fail($"Tipo de comando no soportado: {command.Type}.")
            };

            _log.Info("tool_execution_completed", new
            {
                command = command.Name,
                success = result.Success,
                durationMs = stopwatch.ElapsedMilliseconds
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            _log.Info("tool_execution_cancelled", new
            {
                command = command.Name,
                durationMs = stopwatch.ElapsedMilliseconds
            });
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("tool_execution_failed", ex, new
            {
                command = command.Name,
                durationMs = stopwatch.ElapsedMilliseconds
            });
            return CommandResult.Fail(ex.Message);
        }
    }

    private static CommandResult ExecuteProcess(CommandDefinition command)
    {
        var target = Environment.ExpandEnvironmentVariables(command.Target);
        var arguments = Environment.ExpandEnvironmentVariables(command.Arguments ?? string.Empty);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = target,
            Arguments = arguments,
            UseShellExecute = true
        });

        return process is null
            ? CommandResult.Fail($"Windows no pudo iniciar '{command.Name}'.")
            : CommandResult.Ok($"Ejecutado: {command.Name}");
    }

    private static CommandResult OpenUrl(CommandDefinition command)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = command.Target,
            UseShellExecute = true
        });

        return CommandResult.Ok($"Abierto: {command.Name}");
    }
}
