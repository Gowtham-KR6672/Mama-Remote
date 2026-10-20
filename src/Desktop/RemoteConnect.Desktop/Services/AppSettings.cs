using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteConnect.Protocol;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;
using RemoteConnect.Session.Controller;

namespace RemoteConnect.Desktop.Services;

public sealed record RecentSession(
    string Direction,
    string Target,
    string DeviceId,
    string Alias,
    DateTimeOffset When,
    string Path,
    string Result);

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string Alias { get; set; } = Environment.MachineName;

    public string Theme { get; set; } = "System";

    // Server
    public string? ServerUrl { get; set; }

    // Startup
    public bool AutoStart { get; set; } = true;

    /// <summary>False until the auto-start choice has been applied once (first run / installer).</summary>
    public bool AutoStartApplied { get; set; }

    // Incoming
    public bool AllowIncoming { get; set; } = true;

    public bool AllowControl { get; set; } = true;

    public bool AllowClipboard { get; set; } = true;

    public bool UnattendedEnabled { get; set; }

    public string? UnattendedPasswordHash { get; set; }

    public List<string> UnattendedAllowList { get; set; } = new();

    public bool UnattendedAllowControl { get; set; } = true;

    // Network
    public bool LanListenerEnabled { get; set; } = true;

    public int LanPort { get; set; } = LanAnnouncement.DefaultTcpPort;

    public bool LanDiscoveryEnabled { get; set; } = true;

    // Outgoing
    [JsonConverter(typeof(JsonStringEnumConverter<QualityPreset>))]
    public QualityPreset Quality { get; set; } = QualityPreset.Balanced;

    public Dictionary<string, string> KnownHosts { get; set; } = new();

    public List<RecentSession> Recent { get; set; } = new();
}

/// <summary>Loads/saves settings.json atomically; also implements the known-hosts store for pinning.</summary>
public sealed class SettingsStore : IKnownHostStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();

    public SettingsStore()
    {
        Settings = Load();
    }

    public AppSettings Settings { get; }

    public event Action? Saved;

    public void Save()
    {
        string json;
        lock (_sync)
        {
            if (Settings.Recent.Count > 50)
            {
                Settings.Recent.RemoveRange(50, Settings.Recent.Count - 50);
            }

            json = JsonSerializer.Serialize(Settings, Json);
        }

        var path = AppPaths.SettingsFile;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
        Saved?.Invoke();
    }

    public string? RegistrationKey
    {
        get
        {
            var bytes = ProtectedFileStore.TryRead(AppPaths.RegistrationKeyFile);
            return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                if (File.Exists(AppPaths.RegistrationKeyFile))
                {
                    File.Delete(AppPaths.RegistrationKeyFile);
                }
            }
            else
            {
                ProtectedFileStore.Write(AppPaths.RegistrationKeyFile, System.Text.Encoding.UTF8.GetBytes(value));
            }
        }
    }

    public void AddRecent(RecentSession session)
    {
        lock (_sync)
        {
            Settings.Recent.Insert(0, session);
        }

        TrySave();
    }

    public string? GetFingerprint(string key)
    {
        lock (_sync)
        {
            return Settings.KnownHosts.TryGetValue(key, out var fp) ? fp : null;
        }
    }

    public void Remember(string key, string fingerprint)
    {
        lock (_sync)
        {
            if (Settings.KnownHosts.TryGetValue(key, out var existing) && existing == fingerprint)
            {
                return;
            }

            Settings.KnownHosts[key] = fingerprint;
        }

        TrySave();
    }

    public void ForgetKey(string key)
    {
        lock (_sync)
        {
            Settings.KnownHosts.Remove(key);
        }

        TrySave();
    }

    public void Forget(string deviceId)
    {
        lock (_sync)
        {
            Settings.KnownHosts.Remove("id:" + deviceId);
            foreach (var key in Settings.KnownHosts.Where(kv => kv.Key.StartsWith("addr:", StringComparison.Ordinal)
                && DeviceIds.TryFromFingerprint(kv.Value, out var id) && id == deviceId).Select(kv => kv.Key).ToList())
            {
                Settings.KnownHosts.Remove(key);
            }
        }

        TrySave();
    }

    public void TrySave()
    {
        try
        {
            Save();
        }
        catch (IOException)
        {
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", overwrite: true);
        }

        return new AppSettings();
    }
}
