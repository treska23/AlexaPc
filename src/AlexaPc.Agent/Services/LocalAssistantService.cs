using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class LocalAssistantService
{
    private const string SpokenPrefix = "[bardo] ";

    private readonly CommandConfigurationService _commandConfigurationService;
    private readonly CommandExecutionService _executionService;
    private readonly LocalLlamaService _llamaService;
    private readonly AppLogService _log;

    public LocalAssistantService(
        CommandConfigurationService commandConfigurationService,
        CommandExecutionService executionService,
        LocalLlamaService llamaService,
        AppLogService log)
    {
        _commandConfigurationService = commandConfigurationService;
        _executionService = executionService;
        _llamaService = llamaService;
        _log = log;
    }

    public async Task<CommandResult> HandleAsync(
        string utterance,
        CancellationToken cancellationToken = default)
    {
        var commands = _commandConfigurationService.Load();
        _log.Info("local_assistant_started", new { utterance });

        try
        {
            var decision = await _llamaService
                .DecideAsync(utterance, commands, cancellationToken)
                .ConfigureAwait(false);

            if (decision.Kind is "reply" or "clarify")
            {
                _log.Info("local_assistant_reply_ready", new { decision.Kind });
                return CommandResult.Ok(SpokenPrefix + decision.Reply);
            }

            foreach (var commandName in decision.Commands)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsDangerousCommandExplicitlyRequested(commandName, utterance))
                {
                    _log.Info("local_assistant_tool_rejected", new
                    {
                        command = commandName,
                        reason = "explicit_confirmation_required"
                    });
                    return CommandResult.Ok(
                        SpokenPrefix + "Ese comando necesita una petición explícita para ejecutarse.");
                }

                var definition = commands.First(command =>
                    string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase));

                var result = await _executionService
                    .ExecuteAsync(definition, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Success)
                {
                    _log.Info("local_assistant_tool_failed", new { command = commandName });
                    return result;
                }
            }

            var reply = string.IsNullOrWhiteSpace(decision.Reply) ? "Hecho." : decision.Reply;
            return CommandResult.Ok(SpokenPrefix + reply);
        }
        catch (TimeoutException ex)
        {
            _log.Error("local_assistant_timed_out", ex, new { utterance });
            return CommandResult.Fail("El asistente local no respondió a tiempo. Vuelve a intentarlo.");
        }
        catch (OperationCanceledException ex)
        {
            _log.Error("local_assistant_cancelled", ex, new { utterance });
            return CommandResult.Fail("La orden se canceló antes de terminar.");
        }
        catch (Exception ex)
        {
            _log.Error("local_assistant_failed", ex);
            return CommandResult.Fail("El asistente local no pudo completar la petición.");
        }
    }

    private static bool IsDangerousCommandExplicitlyRequested(string commandName, string utterance)
    {
        var normalizedCommand = commandName.ToLowerInvariant();
        var normalizedUtterance = utterance.ToLowerInvariant();

        if (normalizedCommand.StartsWith("apaga", StringComparison.Ordinal))
        {
            return normalizedUtterance.Contains("apag", StringComparison.Ordinal);
        }

        if (normalizedCommand.StartsWith("reinicia", StringComparison.Ordinal))
        {
            return normalizedUtterance.Contains("reinici", StringComparison.Ordinal);
        }

        if (normalizedCommand.StartsWith("suspende", StringComparison.Ordinal))
        {
            return normalizedUtterance.Contains("suspend", StringComparison.Ordinal);
        }

        if (normalizedCommand.StartsWith("bloquea", StringComparison.Ordinal))
        {
            return normalizedUtterance.Contains("bloque", StringComparison.Ordinal);
        }

        return true;
    }
}
