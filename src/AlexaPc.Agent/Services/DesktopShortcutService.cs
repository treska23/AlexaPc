using System.Diagnostics;
using System.Text;

namespace AlexaPc.Agent.Services;

public sealed class DesktopShortcutService
{
    public void EnsureShortcut()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(desktop, "AlexaPc.lnk");
            if (File.Exists(shortcutPath))
            {
                return;
            }

            var workingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            var script = $"$ws=New-Object -ComObject WScript.Shell;" +
                         $"$s=$ws.CreateShortcut('{Escape(shortcutPath)}');" +
                         $"$s.TargetPath='{Escape(executablePath)}';" +
                         $"$s.WorkingDirectory='{Escape(workingDirectory)}';" +
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
        catch
        {
            // El acceso directo es una comodidad; nunca debe impedir que arranque AlexaPc.
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
