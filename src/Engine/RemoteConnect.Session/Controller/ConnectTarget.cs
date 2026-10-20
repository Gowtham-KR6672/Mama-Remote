using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using RemoteConnect.Security;

namespace RemoteConnect.Session.Controller;

/// <summary>What the user typed in "Remote Address": a 9-digit device ID or a host/IP with optional port.</summary>
public sealed record ConnectTarget(string? DeviceId, string? Host, int Port)
{
    public bool IsDeviceId => DeviceId is not null;

    /// <summary>Key under which the pinned fingerprint is stored.</summary>
    public string PinKey => IsDeviceId ? "id:" + DeviceId : $"addr:{Host!.ToLowerInvariant()}:{Port}";

    public override string ToString() => IsDeviceId ? DeviceIds.Format(DeviceId!) : Host!.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

public static partial class ConnectTargetParser
{
    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostnameRegex();

    public static bool TryParse(string? input, int defaultPort, out ConnectTarget target, out string error)
    {
        target = null!;
        error = string.Empty;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Enter a RemoteConnect ID (e.g. 123 456 789) or an IP address.";
            return false;
        }

        if (DeviceIds.TryNormalize(text, out var id))
        {
            target = new ConnectTarget(id, null, 0);
            return true;
        }

        string host = text;
        int port = defaultPort;

        // [IPv6]:port
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0)
            {
                error = "Invalid IPv6 address.";
                return false;
            }

            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.StartsWith(':') && !TryPort(rest[1..], out port))
            {
                error = "Invalid port.";
                return false;
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            int colon = text.IndexOf(':');
            host = text[..colon];
            if (!TryPort(text[(colon + 1)..], out port))
            {
                error = "Invalid port.";
                return false;
            }
        }

        // IPAddress.TryParse also accepts legacy forms like "1234" or "10.1"; require a dotted quad for IPv4.
        if (IPAddress.TryParse(host, out var ip)
            && (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || host.Count(c => c == '.') == 3))
        {
            target = new ConnectTarget(null, ip.ToString(), port);
            return true;
        }

        if (HostnameRegex().IsMatch(host) && !host.All(char.IsDigit))
        {
            target = new ConnectTarget(null, host, port);
            return true;
        }

        error = "That is neither a 9-digit RemoteConnect ID nor a valid IP address / host name.";
        return false;
    }

    private static bool TryPort(string s, out int port)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536;
}
