namespace AlexaPc.Agent.Models;

public sealed class CommandDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public CommandType Type { get; init; }
    public string Target { get; init; } = string.Empty;
    public string? Arguments { get; init; }
}
