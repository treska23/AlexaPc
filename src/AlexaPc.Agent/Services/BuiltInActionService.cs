using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
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

    private const uint WmAppCommand = 0x0319;
    private const int AppCommandMediaPlay = 46;
    private const int AppCommandMediaPause = 47;

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
                return CommandResult.Ok("Hecho.");
            case "media.next":
                PressMediaKey(VkMediaNextTrack);
                return CommandResult.Ok("Hecho.");
            case "media.previous":
                PressMediaKey(VkMediaPrevTrack);
                return CommandResult.Ok("Hecho.");
            case "volume.mute":
                PressMediaKey(VkVolumeMute);
                return CommandResult.Ok("Hecho.");
            case "volume.up":
                PressMediaKey(VkVolumeUp);
                return CommandResult.Ok("Hecho.");
            case "volume.down":
                PressMediaKey(VkVolumeDown);
                return CommandResult.Ok("Hecho.");
            case "system.mac":
                return GetWakeOnLanMacAddress();
            case "system.lock":
                StartSystemProcess("rundll32.exe", "user32.dll,LockWorkStation");
                return CommandResult.Ok("Ordenador bloqueado.");
            case "system.sleep":
                StartSystemProcess(
                    "cmd.exe",
                    "/c \"timeout /t 5 /nobreak >nul & rundll32.exe powrprof.dll,SetSuspendState 0,0,0\"");
                return CommandResult.Ok("El ordenador se suspenderá en cinco segundos.");
            case "system.shutdown":
                StartSystemProcess("shutdown.exe", "/s /t 5");
                return CommandResult.Ok("El ordenador se apagará en cinco segundos.");
            case "system.restart":
                StartSystemProcess("shutdown.exe", "/r /t 5");
                return CommandResult.Ok("El ordenador se reiniciará en cinco segundos.");
            default:
                return CommandResult.Fail($"Acción integrada desconocida: {action}");
        }
    }

    private static CommandResult GetWakeOnLanMacAddress()
    {
        try
        {
            var candidate = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .Where(adapter => adapter.GetPhysicalAddress().GetAddressBytes().Length == 6)
                .Select(adapter => new
                {
                    Adapter = adapter,
                    HasGateway = adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                        !IPAddress.Any.Equals(gateway.Address) &&
                        !IPAddress.IPv6Any.Equals(gateway.Address))
                })
                .OrderByDescending(item => item.HasGateway)
                .ThenByDescending(item => item.Adapter.OperationalStatus == OperationalStatus.Up)
                .ThenByDescending(item => item.Adapter.Speed)
                .FirstOrDefault();

            if (candidate is null)
            {
                return CommandResult.Fail("No he encontrado un adaptador Ethernet válido para Wake-on-LAN.");
            }

            byte[] address = candidate.Adapter.GetPhysicalAddress().GetAddressBytes();
            string mac = string.Join(":", address.Select(value => value.ToString("X2")));
            return CommandResult.Ok($"MAC WOL: {mac}");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"No he podido obtener la MAC Wake-on-LAN: {ex.Message}");
        }
    }

    private static async Task<CommandResult> SetPlaybackStateAsync(bool play, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession();

            if (session is not null)
            {
                var status = session.GetPlaybackInfo().PlaybackStatus;

                if (play && status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return CommandResult.Ok("La reproducción ya estaba activa.");
                }

                if (!play && status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    return CommandResult.Ok("La reproducción ya estaba pausada.");
                }

                var success = play
                    ? await session.TryPlayAsync()
                    : await session.TryPauseAsync();

                if (success)
                {
                    return CommandResult.Ok(play
                        ? "Reproducción iniciada o reanudada."
                        : "Reproducción pausada.");
                }
            }
        }
        catch
        {
            // Some browsers/players do not expose a controllable GSMTC session.
            // Fall through to the native WM_APPCOMMAND path below.
        }

        if (SendDirectMediaCommand(play))
        {
            return CommandResult.Ok(play
                ? "Orden directa de reproducción enviada."
                : "Orden directa de pausa enviada.");
        }

        return CommandResult.Fail(play
            ? "Windows no pudo iniciar o reanudar la reproducción."
            : "Windows no pudo pausar la reproducción.");
    }

    private static bool SendDirectMediaCommand(bool play)
    {
        var targetWindow = GetForegroundWindow();
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        var command = play ? AppCommandMediaPlay : AppCommandMediaPause;
        var lParam = new IntPtr(command << 16);
        return PostMessage(targetWindow, WmAppCommand, targetWindow, lParam);
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
