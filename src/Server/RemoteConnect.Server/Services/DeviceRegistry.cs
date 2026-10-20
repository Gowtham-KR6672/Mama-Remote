using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;

namespace RemoteConnect.Server.Services;

public sealed class DeviceRecord
{
    public required string DeviceId { get; init; }

    public required string Fingerprint { get; init; }

    public string Alias { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; set; }

    public string[] LanEndpoints { get; set; } = Array.Empty<string>();

    [JsonIgnore]
    public string? ConnectionId { get; set; }

    [JsonIgnore]
    public bool Online => ConnectionId is not null;
}

/// <summary>
/// ID → key binding plus presence. The first key to register an ID owns it (IDs are derived from keys, so a
/// conflict means a collision or an attack). Persisted to a JSON file; presence is in-memory only.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Dictionary<string, DeviceRecord> _devices = new();
    private readonly object _sync = new();
    private readonly string _path;
    private readonly ILogger<DeviceRegistry> _logger;
    private readonly Timer _saveTimer;
    private int _dirty;

    public DeviceRegistry(IOptions<ServerOptions> options, IHostEnvironment env, ILogger<DeviceRegistry> logger)
    {
        _logger = logger;
        var dir = DataPaths.Resolve(options.Value.DataDirectory, env);
        _path = Path.Combine(dir, "devices.json");
        Load();
        _saveTimer = new Timer(_ => SaveIfDirty(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public int OnlineCount
    {
        get
        {
            lock (_sync)
            {
                return _devices.Values.Count(d => d.Online);
            }
        }
    }

    public RegisterError Bind(string deviceId, string fingerprint, string alias, string platform, string appVersion, string[] endpoints, string connectionId, out string? previousConnectionId)
    {
        previousConnectionId = null;
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (_devices.TryGetValue(deviceId, out var existing))
            {
                if (!CertificateFingerprint.AreEqual(existing.Fingerprint, fingerprint))
                {
                    return RegisterError.IdConflict;
                }

                previousConnectionId = existing.ConnectionId;
            }
            else
            {
                existing = new DeviceRecord { DeviceId = deviceId, Fingerprint = fingerprint.ToUpperInvariant(), FirstSeen = now };
                _devices[deviceId] = existing;
            }

            existing.Alias = alias;
            existing.Platform = platform;
            existing.AppVersion = appVersion;
            existing.LanEndpoints = endpoints;
            existing.LastSeen = now;
            existing.ConnectionId = connectionId;
            Interlocked.Exchange(ref _dirty, 1);
            return RegisterError.None;
        }
    }

    public bool SetOffline(string deviceId, string connectionId)
    {
        lock (_sync)
        {
            if (_devices.TryGetValue(deviceId, out var d) && d.ConnectionId == connectionId)
            {
                d.ConnectionId = null;
                d.LastSeen = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref _dirty, 1);
                return true;
            }

            return false;
        }
    }

    public void UpdateEndpoints(string deviceId, string[] endpoints)
    {
        lock (_sync)
        {
            if (_devices.TryGetValue(deviceId, out var d))
            {
                d.LanEndpoints = endpoints;
                d.LastSeen = DateTimeOffset.UtcNow;
            }
        }
    }

    public DeviceRecord? Get(string deviceId)
    {
        lock (_sync)
        {
            return _devices.TryGetValue(deviceId, out var d) ? Snapshot(d) : null;
        }
    }

    public IReadOnlyList<DeviceRecord> All()
    {
        lock (_sync)
        {
            return _devices.Values.Select(Snapshot).OrderBy(d => d.DeviceId).ToList();
        }
    }

    /// <summary>Admin revocation: forget the binding. Returns the live connection to abort, if any.</summary>
    public bool Remove(string deviceId, out string? connectionId)
    {
        lock (_sync)
        {
            connectionId = null;
            if (!_devices.Remove(deviceId, out var d))
            {
                return false;
            }

            connectionId = d.ConnectionId;
            Interlocked.Exchange(ref _dirty, 1);
            return true;
        }
    }

    private static DeviceRecord Snapshot(DeviceRecord d) => new()
    {
        DeviceId = d.DeviceId,
        Fingerprint = d.Fingerprint,
        Alias = d.Alias,
        Platform = d.Platform,
        AppVersion = d.AppVersion,
        FirstSeen = d.FirstSeen,
        LastSeen = d.LastSeen,
        LanEndpoints = d.LanEndpoints,
        ConnectionId = d.ConnectionId,
    };

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<DeviceRecord>>(File.ReadAllText(_path), Json) ?? new();
            foreach (var d in list.Where(d => DeviceIds.TryNormalize(d.DeviceId, out _)))
            {
                d.LanEndpoints = Array.Empty<string>();
                _devices[d.DeviceId] = d;
            }

            _logger.LogInformation("Loaded {Count} device bindings", _devices.Count);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "Device registry {Path} is unreadable; starting empty (file kept)", _path);
            File.Copy(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
        }
    }

    public void SaveIfDirty()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        try
        {
            string json;
            lock (_sync)
            {
                json = JsonSerializer.Serialize(_devices.Values.OrderBy(d => d.DeviceId).ToList(), Json);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _logger.LogWarning("Saving device registry failed: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        SaveIfDirty();
    }
}

internal static class DataPaths
{
    public static string Resolve(string configured, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
