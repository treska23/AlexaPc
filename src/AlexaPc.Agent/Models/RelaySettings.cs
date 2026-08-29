namespace AlexaPc.Agent.Models;

public sealed class RelaySettings
{
    public bool Enabled { get; set; } = true;
    public string RelayUrl { get; set; } = "ws://localhost:5184/ws/agent";
    public string DeviceId { get; set; } = "pc-principal";
    public string DeviceToken { get; set; } = "dev-device-token";
}
