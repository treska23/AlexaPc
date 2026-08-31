using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace AlexaPc.Agent.Services;

public sealed class DesktopShortcutService
{
    public void EnsureShortcuts()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) ||
                !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Una ejecución desde Visual Studio no debe reemplazar el acceso
            // directo ni el inicio automático configurados por la copia Release.
#if !DEBUG
            var workingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            CreateShortcut(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "AlexaPc.lnk"),
                executablePath,
                workingDirectory,
                string.Empty);

            EnsureStartupRegistration(executablePath);
#endif
        }
        catch
        {
            // Los accesos directos son una comodidad; nunca deben impedir que arranque AlexaPc.
        }
    }

    private static void EnsureStartupRegistration(string executablePath)
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var runKey = Registry.CurrentUser.CreateSubKey(runKeyPath);
        runKey?.SetValue(
            "AlexaPc",
            $"\"{executablePath}\" --background",
            RegistryValueKind.String);

        // Versiones anteriores creaban un acceso directo que Windows podía
        // guardar sin TargetPath. El registro Run lo sustituye y evita dobles
        // arranques si aquel archivo todavía existe.
        var legacyShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "AlexaPc.lnk");
        if (File.Exists(legacyShortcut))
        {
            File.Delete(legacyShortcut);
        }
    }

    private static void CreateShortcut(
        string shortcutPath,
        string executablePath,
        string workingDirectory,
        string arguments)
    {
        var script = "$ws=New-Object -ComObject WScript.Shell;" +
                         $"$s=$ws.CreateShortcut('{Escape(shortcutPath)}');" +
                         $"$s.TargetPath='{Escape(executablePath)}';" +
                         $"$s.Arguments='{Escape(arguments)}';" +
                         $"$s.WorkingDirectory='{Escape(workingDirectory)}';" +
                         "$s.Description='Controlar el ordenador con Alexa';" +
                         $"$s.IconLocation='{Escape(executablePath)},0';" +
                         "$s.Save();";

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
