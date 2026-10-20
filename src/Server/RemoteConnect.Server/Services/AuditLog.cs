using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RemoteConnect.Server.Services;

/// <summary>One audit record. Never contains secrets (tokens, keys, passwords) or screen/clipboard content.</summary>
public sealed record AuditEvent(
    DateTimeOffset Time,
    string Type,
    string? DeviceId,
    string? PeerDeviceId,
    string? SessionId,
    string? Address,
    string? Detail);

/// <summary>Append-only daily JSON-lines files under {DataDirectory}/audit with a retention limit.</summary>
public sealed class AuditLog
{
    private const int RecentCapacity = 2000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _dir;
    private readonly object _sync = new();
    private readonly LinkedList<AuditEvent> _recent = new();
    private readonly int _retentionDays;
    private readonly ILogger<AuditLog> _logger;
    private DateOnly _lastCleanup;

    public AuditLog(IOptions<ServerOptions> options, IHostEnvironment env, ILogger<AuditLog> logger)
    {
        _logger = logger;
        _retentionDays = Math.Max(1, options.Value.AuditRetentionDays);
        _dir = Path.Combine(DataPaths.Resolve(options.Value.DataDirectory, env), "audit");
        Directory.CreateDirectory(_dir);
        Cleanup();
    }

    public void Write(string type, string? deviceId = null, string? peer = null, string? sessionId = null, string? address = null, string? detail = null)
    {
        var e = new AuditEvent(DateTimeOffset.UtcNow, type, deviceId, peer, sessionId, address, detail);
        var line = JsonSerializer.Serialize(e, Json);
        lock (_sync)
        {
            _recent.AddLast(e);
            if (_recent.Count > RecentCapacity)
            {
                _recent.RemoveFirst();
            }

            try
            {
                File.AppendAllText(Path.Combine(_dir, $"audit-{e.Time:yyyyMMdd}.jsonl"), line + Environment.NewLine);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Audit write failed: {Message}", ex.Message);
            }

            if (DateOnly.FromDateTime(e.Time.UtcDateTime) != _lastCleanup)
            {
                Cleanup();
            }
        }

        _logger.LogInformation("AUDIT {Type} device={Device} peer={Peer} session={Session} {Detail}", type, deviceId, peer, sessionId, detail);
    }

    public IReadOnlyList<AuditEvent> Query(string? deviceId, DateTimeOffset? since, int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var results = new List<AuditEvent>();
        var from = since ?? DateTimeOffset.UtcNow.AddDays(-7);
        var files = Directory.GetFiles(_dir, "audit-*.jsonl").OrderDescending();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file)["audit-".Length..];
            if (DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                && day.AddDays(1) < from.UtcDateTime)
            {
                break;
            }

            string[] lines;
            lock (_sync)
            {
                lines = File.ReadAllLines(file);
            }

            for (int i = lines.Length - 1; i >= 0 && results.Count < limit; i--)
            {
                AuditEvent? e;
                try
                {
                    e = JsonSerializer.Deserialize<AuditEvent>(lines[i], Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (e is null || e.Time < from)
                {
                    continue;
                }

                if (deviceId is null || e.DeviceId == deviceId || e.PeerDeviceId == deviceId)
                {
                    results.Add(e);
                }
            }

            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    private void Cleanup()
    {
        _lastCleanup = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var file in Directory.GetFiles(_dir, "audit-*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
