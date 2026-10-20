using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;

namespace RemoteConnect.LocalIpc;

/// <summary>
/// Settings of the machine-wide (service-mode) host. Written only by an administrator through
/// <c>RemoteConnect.HostService.exe</c>; read by the session agent. Unattended access is off until a password is set.
/// </summary>
public sealed class ServiceModeSettings
{
    public const int MaxAliasLength = 64;

    public int SchemaVersion { get; set; } = 1;

    public string Alias { get; set; } = Environment.MachineName;

    public string? ServerUrl { get; set; }

    public bool LanListenerEnabled { get; set; } = true;

    public int LanPort { get; set; } = LanAnnouncement.DefaultTcpPort;

    public bool LanDiscoveryEnabled { get; set; } = true;

    public bool UnattendedEnabled { get; set; }

    public string? UnattendedPasswordHash { get; set; }

    /// <summary>Controller device IDs allowed to use the password. Empty = any device that knows it.</summary>
    public List<string> UnattendedAllowList { get; set; } = new();

    public bool UnattendedAllowControl { get; set; } = true;

    /// <summary>In service mode this is the only way in: nobody can accept a prompt at the sign-in screen.</summary>
    [JsonIgnore]
    public bool UnattendedReady => UnattendedEnabled && !string.IsNullOrEmpty(UnattendedPasswordHash);

    /// <summary>Clamps values an administrator may have edited by hand. Never turns anything on.</summary>
    public ServiceModeSettings Normalize()
    {
        Alias = string.IsNullOrWhiteSpace(Alias) ? Environment.MachineName : Alias.Trim();
        if (Alias.Length > MaxAliasLength)
        {
            Alias = Alias[..MaxAliasLength];
        }

        if (LanPort is <= 0 or > 65535)
        {
            LanPort = LanAnnouncement.DefaultTcpPort;
        }

        ServerUrl = string.IsNullOrWhiteSpace(ServerUrl) ? null : ServerUrl.Trim();
        UnattendedAllowList = (UnattendedAllowList ?? new List<string>())
            .Select(id => DeviceIds.TryNormalize(id, out var n) ? n : null)
            .OfType<string>()
            .Distinct()
            .ToList();
        if (string.IsNullOrEmpty(UnattendedPasswordHash))
        {
            UnattendedPasswordHash = null;
            UnattendedEnabled = false;
        }

        return this;
    }
}

public static class ServiceModeSettingsFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Missing file → defaults. Unreadable or malformed file → defaults too (unattended access off), with the reason
    /// in <paramref name="error"/>: a broken file must never leave the computer more open than before.
    /// </summary>
    public static ServiceModeSettings Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                return new ServiceModeSettings();
            }

            var settings = JsonSerializer.Deserialize<ServiceModeSettings>(File.ReadAllText(path), Json);
            if (settings is null)
            {
                error = "Settings file is empty.";
                return new ServiceModeSettings();
            }

            return settings.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return new ServiceModeSettings();
        }
    }

    public static void Save(string path, ServiceModeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalize(), Json));
        File.Move(tmp, path, overwrite: true);
    }
}
