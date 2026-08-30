using System.IO;
using System.Text.Json;

namespace AlexaPc.Agent.Services;

public sealed class AppLogService
{
    private readonly object _gate = new();
    private readonly string _logDirectory;

    public AppLogService()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlexaPc",
            "logs");

        try
        {
            Directory.CreateDirectory(_logDirectory);
            DeleteOldLogs();
        }
        catch
        {
        }
    }

    public string LogDirectory => _logDirectory;

    public void Info(string eventName, object? details = null)
        => Write("info", eventName, details);

    public void Error(string eventName, Exception exception, object? details = null)
        => Write("error", eventName, new
        {
            errorType = exception.GetType().Name,
            error = exception.Message,
            details
        });

    private void Write(string level, string eventName, object? details)
    {
        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                level,
                eventName,
                details
            });

            var path = Path.Combine(_logDirectory, $"alexapc-{DateTime.Now:yyyyMMdd}.log");
            lock (_gate)
            {
                File.AppendAllText(path, entry + Environment.NewLine);
            }
        }
        catch
        {
            // El diagnóstico nunca debe interrumpir el control del ordenador.
        }
    }

    private void DeleteOldLogs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        foreach (var path in Directory.EnumerateFiles(_logDirectory, "alexapc-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
