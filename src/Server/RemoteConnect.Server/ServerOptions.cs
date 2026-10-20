namespace RemoteConnect.Server;

/// <summary>Bound from the "RemoteConnect" configuration section (appsettings / environment variables).</summary>
public sealed class ServerOptions
{
    public const string Section = "RemoteConnect";

    /// <summary>Device registry and audit logs. Relative paths are resolved against the content root.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Shared key devices must present to register. Strongly recommended for any internet-facing server so only
    /// your devices can use it. Set via environment variable RemoteConnect__RegistrationKey (never commit it).
    /// </summary>
    public string? RegistrationKey { get; set; }

    /// <summary>Enables /api/v1/admin/* when set (header X-Admin-Key). Disabled when empty.</summary>
    public string? AdminApiKey { get; set; }

    public int RelayPairTimeoutSeconds { get; set; } = 30;

    public int MaxConcurrentRelays { get; set; } = 200;

    /// <summary>0 = unlimited.</summary>
    public long MaxRelayBytesPerSession { get; set; }

    public int AuditRetentionDays { get; set; } = 90;

    public int ResolvePerMinute { get; set; } = 30;

    public int RelayRequestsPerMinute { get; set; } = 10;

    /// <summary>Per-IP limit for new hub/relay HTTP connections.</summary>
    public int ConnectionsPerMinutePerIp { get; set; } = 120;

    /// <summary>Reverse proxies whose X-Forwarded-For header is trusted (IP addresses).</summary>
    public string[] TrustedProxies { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Trust X-Forwarded-For/Proto from any source. Only for platforms where the app is reachable exclusively
    /// through the platform's proxy (Render, Railway, Fly.io, Azure App Service …) and the proxy IPs are unknown.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }
}
