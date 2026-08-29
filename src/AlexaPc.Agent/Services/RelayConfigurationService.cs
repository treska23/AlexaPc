using System.IO;
using System.Text.Json;
using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class RelayConfigurationService
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public RelayConfigurationService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlexaPc");

        Directory.CreateDirectory(directory);
        ConfigurationPath = Path.Combine(directory, "relay.json");
    }

    public string ConfigurationPath { get; }

    public RelaySettings Load()
    {
        if (!File.Exists(ConfigurationPath))
        {
            Save(new RelaySettings());
        }

        var json = File.ReadAllText(ConfigurationPath);
        return JsonSerializer.Deserialize<RelaySettings>(json, _jsonOptions) ?? new RelaySettings();
    }

    private void Save(RelaySettings settings)
        => File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(settings, _jsonOptions));
}
