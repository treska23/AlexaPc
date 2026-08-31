using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Bardo.Mobile.Infrastructure;

internal sealed class WakeOnLanClient
{
    public Task WakeAsync(
        string macAddress,
        CancellationToken cancellationToken = default)
    {
        byte[] mac = ParseMac(macAddress);
        byte[] packet = BuildMagicPacket(mac);
        var destinations = new[]
        {
            new IPEndPoint(IPAddress.Broadcast, 9),
            new IPEndPoint(IPAddress.Broadcast, 7)
        };

        using var client = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };

        foreach (IPEndPoint destination in destinations)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                client.Send(packet, packet.Length, destination);
            }
        }

        return Task.CompletedTask;
    }

    public static bool IsValidMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Regex.IsMatch(
            value.Trim(),
            @"^(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$|^[0-9A-Fa-f]{12}$",
            RegexOptions.CultureInvariant);
    }

    public static string NormalizeMac(string value)
    {
        byte[] bytes = ParseMac(value);
        return string.Join(":", bytes.Select(valueByte => valueByte.ToString("X2")));
    }

    private static byte[] ParseMac(string value)
    {
        string compact = Regex.Replace(value ?? string.Empty, "[^0-9A-Fa-f]", string.Empty);
        if (compact.Length != 12)
        {
            throw new ArgumentException("La MAC del PC no tiene un formato válido.", nameof(value));
        }

        var bytes = new byte[6];
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(compact.Substring(index * 2, 2), 16);
        }

        return bytes;
    }

    private static byte[] BuildMagicPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * mac.Length];
        Array.Fill(packet, (byte)0xFF, 0, 6);

        for (int repeat = 0; repeat < 16; repeat++)
        {
            Buffer.BlockCopy(mac, 0, packet, 6 + repeat * mac.Length, mac.Length);
        }

        return packet;
    }
}
