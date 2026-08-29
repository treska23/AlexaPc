using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlexaPc.Agent.Models;

namespace AlexaPc.Agent.Services;

public sealed class CommandConfigurationService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CommandConfigurationService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlexaPc");

        Directory.CreateDirectory(directory);
        ConfigurationPath = Path.Combine(directory, "commands.json");
    }

    public string ConfigurationPath { get; }

    public IReadOnlyList<CommandDefinition> Load()
    {
        if (!File.Exists(ConfigurationPath))
        {
            Save(CreateDefaults());
        }

        var json = File.ReadAllText(ConfigurationPath);
        var document = JsonSerializer.Deserialize<CommandConfigurationDocument>(json, _jsonOptions)
                       ?? new CommandConfigurationDocument();

        var commands = document.Commands.ToList();
        if (MigrateAndAddMissingDefaults(commands))
        {
            Save(commands);
        }

        return commands;
    }

    private void Save(IReadOnlyList<CommandDefinition> commands)
    {
        var document = new CommandConfigurationDocument
        {
            Commands = commands.ToList()
        };

        File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(document, _jsonOptions));
    }

    private static bool MigrateAndAddMissingDefaults(List<CommandDefinition> commands)
    {
        var changed = false;

        var pauseIndex = commands.FindIndex(command =>
            string.Equals(command.Name, "pausa", StringComparison.OrdinalIgnoreCase));

        if (pauseIndex >= 0 &&
            commands[pauseIndex].Type == CommandType.BuiltIn &&
            string.Equals(commands[pauseIndex].Target, "media.playPause", StringComparison.OrdinalIgnoreCase))
        {
            commands[pauseIndex] = new CommandDefinition
            {
                Name = "pausa",
                Description = "Pausa el contenido multimedia actual.",
                Type = CommandType.BuiltIn,
                Target = "media.pause"
            };
            changed = true;
        }

        foreach (var defaultCommand in CreateDefaults())
        {
            if (commands.Any(command =>
                    string.Equals(command.Name, defaultCommand.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            commands.Add(defaultCommand);
            changed = true;
        }

        return changed;
    }

    private static IReadOnlyList<CommandDefinition> CreateDefaults() =>
    [
        new()
        {
            Name = "bloc de notas",
            Description = "Abre el Bloc de notas de Windows.",
            Type = CommandType.Process,
            Target = "notepad.exe"
        },
        new()
        {
            Name = "youtube",
            Description = "Abre YouTube en el navegador predeterminado.",
            Type = CommandType.Url,
            Target = "https://www.youtube.com"
        },
        new()
        {
            Name = "pausa",
            Description = "Pausa el contenido multimedia actual.",
            Type = CommandType.BuiltIn,
            Target = "media.pause"
        },
        new()
        {
            Name = "reanuda",
            Description = "Reanuda el contenido multimedia pausado.",
            Type = CommandType.BuiltIn,
            Target = "media.play"
        },
        new()
        {
            Name = "reproduce",
            Description = "Inicia o reanuda la reproducción multimedia actual.",
            Type = CommandType.BuiltIn,
            Target = "media.play"
        },
        new()
        {
            Name = "siguiente",
            Description = "Pasa a la siguiente pista multimedia.",
            Type = CommandType.BuiltIn,
            Target = "media.next"
        },
        new()
        {
            Name = "silencio",
            Description = "Activa o desactiva el silencio del sistema.",
            Type = CommandType.BuiltIn,
            Target = "volume.mute"
        },
        new()
        {
            Name = "sube volumen",
            Description = "Sube un paso el volumen de Windows.",
            Type = CommandType.BuiltIn,
            Target = "volume.up"
        },
        new()
        {
            Name = "baja volumen",
            Description = "Baja un paso el volumen de Windows.",
            Type = CommandType.BuiltIn,
            Target = "volume.down"
        },
        new()
        {
            Name = "bloquea ordenador",
            Description = "Bloquea la sesión actual de Windows.",
            Type = CommandType.BuiltIn,
            Target = "system.lock"
        },
        new()
        {
            Name = "suspende ordenador",
            Description = "Suspende el PC.",
            Type = CommandType.BuiltIn,
            Target = "system.sleep"
        },
        new()
        {
            Name = "apaga ordenador",
            Description = "Apaga Windows inmediatamente.",
            Type = CommandType.BuiltIn,
            Target = "system.shutdown"
        },
        new()
        {
            Name = "reinicia ordenador",
            Description = "Reinicia Windows inmediatamente.",
            Type = CommandType.BuiltIn,
            Target = "system.restart"
        }
    ];
}
