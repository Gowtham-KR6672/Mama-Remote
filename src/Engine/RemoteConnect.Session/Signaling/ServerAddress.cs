using System.Net;
using System.Net.Sockets;

namespace RemoteConnect.Session.Signaling;

/// <summary>
/// Validates the self-hosted server address. HTTPS is required, except for loopback and private-network
/// addresses (LAN trials). Peer sessions are end-to-end TLS either way; this protects registration/presence.
/// </summary>
public static class ServerAddress
{
    public static bool TryParse(string? input, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "No server configured.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            error = "Server address must look like https://remote.example.com";
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp && !IsLocalHost(parsed.Host))
        {
            error = "Use https:// for servers outside your local network.";
            return false;
        }

        uri = new UriBuilder(parsed.Scheme, parsed.Host, parsed.Port).Uri;
        return true;
    }

    public static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }

    public static Uri RelayUri(Uri server, string sessionId)
    {
        var scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return new UriBuilder(scheme, server.Host, server.Port, $"/relay/{Uri.EscapeDataString(sessionId)}").Uri;
    }
}
