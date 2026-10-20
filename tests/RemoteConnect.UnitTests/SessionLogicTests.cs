using System.Net;
using System.Text;
using RemoteConnect.Security;
using RemoteConnect.Session.Controller;
using RemoteConnect.Session.Discovery;
using RemoteConnect.Session.Host;
using RemoteConnect.Session.Signaling;

namespace RemoteConnect.UnitTests;

public class SessionLogicTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("482 913 207", "482913207", null, 0)]
    [InlineData("192.168.1.20", null, "192.168.1.20", 47070)]
    [InlineData("192.168.1.20:5000", null, "192.168.1.20", 5000)]
    [InlineData("[fe80::1]:6000", null, "fe80::1", 6000)]
    [InlineData("office-pc.local", null, "office-pc.local", 47070)]
    public void Connect_target_parsing(string input, string? id, string? host, int port)
    {
        Assert.True(ConnectTargetParser.TryParse(input, 47070, out var t, out _));
        Assert.Equal(id, t.DeviceId);
        Assert.Equal(host, t.Host);
        Assert.Equal(port, t.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("host:99999")]
    [InlineData("bad host!")]
    public void Connect_target_rejects_garbage(string input) => Assert.False(ConnectTargetParser.TryParse(input, 47070, out _, out _));

    [Theory]
    [InlineData("remote.example.com", true, "https://remote.example.com/")]
    [InlineData("https://remote.example.com:8443/x", true, "https://remote.example.com:8443/")]
    [InlineData("http://192.168.1.5:5080", true, "http://192.168.1.5:5080/")]
    [InlineData("http://localhost:5080", true, "http://localhost:5080/")]
    [InlineData("http://remote.example.com", false, null)]
    [InlineData("ftp://x", false, null)]
    public void Server_address_requires_https_outside_local_networks(string input, bool ok, string? expected)
    {
        Assert.Equal(ok, ServerAddress.TryParse(input, out var uri, out _));
        if (ok)
        {
            Assert.Equal(expected, uri.ToString());
        }
    }

    [Fact]
    public void Relay_uri_uses_websocket_scheme()
    {
        Assert.Equal("wss://r.example.com/relay/abc", ServerAddress.RelayUri(new Uri("https://r.example.com"), "abc").ToString());
        Assert.Equal("ws://127.0.0.1:5080/relay/abc", ServerAddress.RelayUri(new Uri("http://127.0.0.1:5080"), "abc").ToString());
    }

    [Fact]
    public void Unattended_guard_locks_out_after_repeated_failures_then_expires()
    {
        var time = new ManualTime();
        var guard = new UnattendedGuard(time);
        for (int i = 1; i < UnattendedGuard.MaxFailuresPerDevice; i++)
        {
            var (remaining, locked) = guard.RecordFailure("111111111");
            Assert.False(locked);
            Assert.Equal(UnattendedGuard.MaxFailuresPerDevice - i, remaining);
        }

        Assert.True(guard.RecordFailure("111111111").LockedOut);
        Assert.True(guard.IsLockedOut("111111111"));
        Assert.False(guard.IsLockedOut("222222222"));

        time.Now += UnattendedGuard.LockoutDuration + TimeSpan.FromSeconds(1);
        Assert.False(guard.IsLockedOut("111111111"));
    }

    [Fact]
    public void Unattended_guard_global_limit_stops_id_rotation()
    {
        var guard = new UnattendedGuard(new ManualTime());
        for (int i = 0; i < UnattendedGuard.MaxFailuresGlobal; i++)
        {
            guard.RecordFailure($"{100000000 + i}"); // a fresh ID every attempt
        }

        Assert.True(guard.IsLockedOut("999999999"));
    }

    [Fact]
    public void Host_options_unattended_requires_enable_password_and_allowlist()
    {
        var o = new HostOptions { UnattendedEnabled = true, UnattendedPasswordHash = "x" };
        Assert.True(o.IsUnattendedAvailableFor("111111111"));
        Assert.False((o with { UnattendedEnabled = false }).IsUnattendedAvailableFor("111111111"));
        Assert.False((o with { UnattendedPasswordHash = null }).IsUnattendedAvailableFor("111111111"));
        var listed = o with { UnattendedAllowList = new[] { "222222222" } };
        Assert.False(listed.IsUnattendedAvailableFor("111111111"));
        Assert.True(listed.IsUnattendedAvailableFor("222222222"));
        Assert.False(new HostOptions().UnattendedEnabled); // off by default
    }

    [Fact]
    public void Lan_announcement_with_consistent_identity_is_accepted()
    {
        using var id = DeviceIdentity.Create();
        var json = $"{{\"v\":1,\"deviceId\":\"{id.DeviceId}\",\"alias\":\"Bob\\u0007PC\",\"platform\":\"Windows\",\"port\":47070,\"fingerprint\":\"{id.Fingerprint}\"}}";
        var peer = LanDiscovery.Parse(Encoding.UTF8.GetBytes(json), IPAddress.Parse("192.168.1.9"));
        Assert.NotNull(peer);
        Assert.Equal("BobPC", peer!.Alias); // control characters stripped
        Assert.Equal("192.168.1.9:47070", peer.Endpoint);
    }

    [Fact]
    public void Lan_announcement_claiming_someone_elses_id_is_dropped()
    {
        using var id = DeviceIdentity.Create();
        string otherId = id.DeviceId == "123456789" ? "987654321" : "123456789";
        var json = $"{{\"v\":1,\"deviceId\":\"{otherId}\",\"alias\":\"x\",\"platform\":\"x\",\"port\":47070,\"fingerprint\":\"{id.Fingerprint}\"}}";
        Assert.Null(LanDiscovery.Parse(Encoding.UTF8.GetBytes(json), IPAddress.Loopback));
        Assert.Null(LanDiscovery.Parse(Encoding.UTF8.GetBytes("garbage"), IPAddress.Loopback));
    }
}
