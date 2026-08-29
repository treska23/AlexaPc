using System.Diagnostics;
using System.Runtime.InteropServices;
using AlexaPc.Agent.Models;
using Windows.Media.Control;

namespace AlexaPc.Agent.Services;

public sealed class BuiltInActionService
{
    private const byte KeyEventKeyUp = 0x0002;
    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;

    public async Task<CommandResult> ExecuteAsync(string action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (action.Trim().ToLowerInvariant())
        {
            case "media.play":
                return await SetPlaybackStateAsync(play: true, cancellationToken);
            case "media.pause":
                return await SetPlaybackStateAsync(play: false, cancellationToken);
            case "media.playpause":
                PressMediaKey(VkMediaPlayPause);
                break;
            case "media.next":
                PressMediaKey(VkMediaNextTrack);
                break;
            case "media.previous":
                PressMediaKey(VkMediaPrevTrack);
                break;
            case "volume.mute":
                PressMediaKey(VkVolumeMute);
                break;
            case "volume.up":
                PressMediaKey(VkVolumeUp);
                break;
            case "volume.down":
                PressMediaKey(VkVolumeDown);
                break;
            case "system.lock":
                StartSystemProcess("rundll32.exe", "user32.dll,LockWorkStation");
                break;
            case "system.sleep":
                if (!SetSuspendState(false, false, false))
                {
                    return CommandResult.Fail("Windows no pudo suspender el equipo.");
                }
                break;
            case "system.shutdown":
                StartSystemProcess("shutdown.exe", "/s /t 0");
                break;
            case "system.restart":
                StartSystemProcess("shutdown.exe", "/r /t 0");
                break;
            default:
                return CommandResult.Fail($"Acción integrada desconocida: {action}");
        }

        return CommandResult.Ok($"Acción ejecutada: {action}");
    }

    private static async Task<CommandResult> SetPlaybackStateAsync(bool play, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession();

            if (session is null)
            {
                return CommandResult.Fail("Windows no tiene ninguna sesión multimedia activa.");
            }

            var success = play
                ? await session.TryPlayAsync()
                : await session.TryPauseAsync();

            if (!success)
            {
                return CommandResult.Fail(play
                    ? "Windows no pudo iniciar o reanudar la reproducción."
                    : "Windows no pudo pausar la reproducción.");
            }

            return CommandResult.Ok(play
                ? "Reproducción iniciada o reanudada."
                : "Reproducción pausada.");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"No se pudo controlar la sesión multimedia: {ex.Message}");
        }
    }

    private static void StartSystemProcess(string fileName, string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void PressMediaKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}
