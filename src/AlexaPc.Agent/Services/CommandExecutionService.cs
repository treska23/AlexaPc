using System.Diagnostics;
using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class CommandExecutionService
{
    private readonly BuiltInActionService _builtInActionService = new();

    public Task<CommandResult> ExecuteAsync(CommandDefinition command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return command.Type switch
            {
                CommandType.Process => Task.FromResult(ExecuteProcess(command)),
                CommandType.Url => Task.FromResult(OpenUrl(command)),
                CommandType.BuiltIn => Task.FromResult(_builtInActionService.Execute(command.Target)),
                _ => Task.FromResult(CommandResult.Fail($"Tipo de comando no soportado: {command.Type}."))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResult.Fail(ex.Message));
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
