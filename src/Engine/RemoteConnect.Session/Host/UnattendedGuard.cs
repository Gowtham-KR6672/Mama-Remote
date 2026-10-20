namespace RemoteConnect.Session.Host;

/// <summary>
/// Brute-force protection for the unattended password: per-device lockout plus a global limit (new device IDs are
/// cheap to mint, so the global limit is what actually bounds guessing).
/// </summary>
public sealed class UnattendedGuard
{
    public const int MaxFailuresPerDevice = 5;
    public const int MaxFailuresGlobal = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private const string GlobalKey = "*";
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _sync = new();

    public UnattendedGuard(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool IsLockedOut(string deviceId)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            return IsLocked(GlobalKey, now) || IsLocked(deviceId, now);
        }
    }

    /// <returns>Attempts remaining for this device before lockout, and whether it is now locked.</returns>
    public (int Remaining, bool LockedOut) RecordFailure(string deviceId)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            var device = Bump(deviceId, now, MaxFailuresPerDevice);
            Bump(GlobalKey, now, MaxFailuresGlobal);
            bool locked = IsLocked(GlobalKey, now) || IsLocked(deviceId, now);
            return (locked ? 0 : Math.Max(0, MaxFailuresPerDevice - device.Count), locked);
        }
    }

    public void RecordSuccess(string deviceId)
    {
        lock (_sync)
        {
            _entries.Remove(deviceId);
        }
    }

    private Entry Bump(string key, DateTimeOffset now, int limit)
    {
        if (!_entries.TryGetValue(key, out var e) || now - e.WindowStart > Window)
        {
            e = new Entry { WindowStart = now };
            _entries[key] = e;
        }

        e.Count++;
        if (e.Count >= limit)
        {
            e.LockedUntil = now + LockoutDuration;
            e.Count = 0;
            e.WindowStart = now;
        }

        return e;
    }

    private bool IsLocked(string key, DateTimeOffset now)
        => _entries.TryGetValue(key, out var e) && e.LockedUntil is { } until && until > now;

    private sealed class Entry
    {
        public int Count { get; set; }

        public DateTimeOffset WindowStart { get; set; }

        public DateTimeOffset? LockedUntil { get; set; }
    }
}
