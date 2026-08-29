using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class CommandDispatcher
{
    private readonly CommandConfigurationService _configurationService;
    private readonly CommandExecutionService _executionService;

    public CommandDispatcher(
        CommandConfigurationService configurationService,
        CommandExecutionService executionService)
    {
        _configurationService = configurationService;
        _executionService = executionService;
    }

    public Task<CommandResult> ExecuteAsync(CommandDefinition definition, CancellationToken cancellationToken = default)
        => _executionService.ExecuteAsync(definition, cancellationToken);

    public Task<CommandResult> ExecuteByNameAsync(string commandName, CancellationToken cancellationToken = default)
    {
        var definition = _configurationService
            .Load()
            .FirstOrDefault(command => string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase));

        return definition is null
            ? Task.FromResult(CommandResult.Fail($"No existe el comando '{commandName}'."))
            : _executionService.ExecuteAsync(definition, cancellationToken);
    }
}
