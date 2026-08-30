using AlexaPc.Agent.Models;
using System.Diagnostics;

namespace AlexaPc.Agent.Services;

public sealed class CommandDispatcher
{
    private readonly CommandConfigurationService _configurationService;
    private readonly CommandExecutionService _executionService;
    private readonly LocalAssistantService _assistantService;
    private readonly AppLogService _log;

    public CommandDispatcher(
        CommandConfigurationService configurationService,
        CommandExecutionService executionService,
        LocalAssistantService assistantService,
        AppLogService log)
    {
        _configurationService = configurationService;
        _executionService = executionService;
        _assistantService = assistantService;
        _log = log;
    }

    public Task<CommandResult> ExecuteAsync(CommandDefinition definition, CancellationToken cancellationToken = default)
        => _executionService.ExecuteAsync(definition, cancellationToken);

    public async Task<CommandResult> ExecuteByNameAsync(
        string commandName,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var definition = _configurationService
            .Load()
            .FirstOrDefault(command => string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase));

        var route = definition is null ? "assistant" : "exact";
        _log.Info("command_dispatch_started", new { command = commandName, route });

        var result = definition is not null
            ? await _executionService.ExecuteAsync(definition, cancellationToken).ConfigureAwait(false)
            : await _assistantService.HandleAsync(commandName, cancellationToken).ConfigureAwait(false);

        _log.Info("command_dispatch_completed", new
        {
            command = commandName,
            route,
            success = result.Success,
            durationMs = stopwatch.ElapsedMilliseconds
        });

        return result;
    }
}
