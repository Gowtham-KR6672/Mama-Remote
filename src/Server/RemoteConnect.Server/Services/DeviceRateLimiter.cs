using System.Collections.Concurrent;

namespace RemoteConnect.Server.Services;

/// <summary>Fixed one-minute windows per (action, device). Bounds ID scanning and relay spam.</summary>
public sealed class DeviceRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new();

    public bool TryAcquire(string action, string deviceId, int perMinute)
    {
        var key = action + ":" + deviceId;
        var now = DateTimeOffset.UtcNow;
        var w = _windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) => now - existing.Start >= TimeSpan.FromMinutes(1) ? new Window(now, 1) : existing with { Count = existing.Count + 1 });

        if (_windows.Count > 50_000)
        {
            foreach (var (k, v) in _windows)
            {
                if (now - v.Start > TimeSpan.FromMinutes(2))
                {
                    _windows.TryRemove(k, out _);
                }
            }
        }

        return w.Count <= perMinute;
    }

    private sealed record Window(DateTimeOffset Start, int Count);
}
