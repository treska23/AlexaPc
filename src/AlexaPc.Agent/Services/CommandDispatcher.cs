using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class CommandDispatcher
{
    private readonly CommandConfigurationService _configurationService;
    private readonly CommandExecutionService _executionService;
    private readonly LocalAssistantService _assistantService;

    public CommandDispatcher(
        CommandConfigurationService configurationService,
        CommandExecutionService executionService,
        LocalAssistantService assistantService)
    {
        _configurationService = configurationService;
        _executionService = executionService;
        _assistantService = assistantService;
    }

    public Task<CommandResult> ExecuteAsync(CommandDefinition definition, CancellationToken cancellationToken = default)
        => _executionService.ExecuteAsync(definition, cancellationToken);

    public Task<CommandResult> ExecuteByNameAsync(string commandName, CancellationToken cancellationToken = default)
    {
        var definition = _configurationService
            .Load()
            .FirstOrDefault(command => string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase));

        return definition is not null
            ? _executionService.ExecuteAsync(definition, cancellationToken)
            : _assistantService.HandleAsync(commandName, cancellationToken);
    }
}
