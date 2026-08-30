namespace AlexaPc.Agent.Models;

public sealed class AssistantSettings
{
    public bool Enabled { get; init; } = true;
    public string Provider { get; init; } = "auto";
    public string? BaseUrl { get; init; }
    public string? Model { get; init; }
    public int TimeoutSeconds { get; init; } = 5;
}
