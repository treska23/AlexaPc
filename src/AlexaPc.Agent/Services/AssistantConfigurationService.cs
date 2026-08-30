using System.IO;
using System.Text.Json;
using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class AssistantConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AssistantConfigurationService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlexaPc");

        Directory.CreateDirectory(directory);
        ConfigurationPath = Path.Combine(directory, "assistant.json");
    }

    public string ConfigurationPath { get; }

    public AssistantSettings Load()
    {
        if (!File.Exists(ConfigurationPath))
        {
            var defaults = new AssistantSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(ConfigurationPath);
            return JsonSerializer.Deserialize<AssistantSettings>(json, JsonOptions)
                   ?? new AssistantSettings();
        }
        catch
        {
            return new AssistantSettings();
        }
    }

    public void Save(AssistantSettings settings)
        => File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(settings, JsonOptions));
}
