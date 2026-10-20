using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using RemoteConnect.Protocol;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;
using RemoteConnect.Server;
using RemoteConnect.Session.Controller;
using RemoteConnect.Session.Host;
using RemoteConnect.Session.Signaling;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.IntegrationTests;

/// <summary>
/// The real server on a random local port: key-proof registration, ID resolution and a full session through the
/// WebSocket relay with end-to-end TLS between the two devices.
/// Set RC_TEST_SERVER_URL (+ RC_TEST_SERVER_KEY, optional RC_TEST_ADMIN_KEY) to run the same tests against a
/// deployed server, e.g. a Docker container or https://your-service.onrender.com.
/// </summary>
public class ServerRelayTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _log = new();

    private const string RegistrationKey = "integration-test-key";
    private static readonly Lazy<DeviceIdentity> HostKey = new(DeviceIdentity.Create);
    private static readonly Lazy<DeviceIdentity> ControllerKey = new(DeviceIdentity.Create);

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "rc-test-" + Guid.NewGuid().ToString("N"));
    private static readonly string? ExternalUrl = Environment.GetEnvironmentVariable("RC_TEST_SERVER_URL");
    private WebApplication? _server;
    private Uri? _baseUri;
    private string _key = RegistrationKey;
    private string? _adminKey = "admin-test-key";

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrEmpty(ExternalUrl))
        {
            _baseUri = new Uri(ExternalUrl);
            _key = Environment.GetEnvironmentVariable("RC_TEST_SERVER_KEY") ?? throw new InvalidOperationException("Set RC_TEST_SERVER_KEY.");
            _adminKey = Environment.GetEnvironmentVariable("RC_TEST_ADMIN_KEY");
            return;
        }

        _server = ServerApp.Build(new[]
        {
            "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0",
            $"--RemoteConnect:DataDirectory={_dataDir}",
            $"--RemoteConnect:RegistrationKey={RegistrationKey}",
            "--RemoteConnect:AdminApiKey=admin-test-key",
            "--Logging:LogLevel:Default=Warning",
            "--Logging:LogLevel:RemoteConnect=Debug",
        }, b => b.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(new ListLoggerProvider(_log)));
        await _server.StartAsync();
        var address = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _baseUri = new Uri(address);
    }

    public async Task DisposeAsync()
    {
        foreach (var line in _log)
        {
            output.WriteLine(line);
        }

        if (_server is not null)
        {
            await _server.StopAsync();
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SignalingClient NewClient(DeviceIdentity identity, string alias, string? key = null)
    {
        var client = new SignalingClient(identity, new ListLogger("signal:" + alias, _log));
        _ = client.ConfigureAsync(new SignalingOptions(_baseUri!.ToString(), key ?? _key, alias, "Test", "1.0"));
        return client;
    }

    [Fact]
    public async Task Health_and_info_endpoints_respond()
    {
        using var http = new HttpClient { BaseAddress = _baseUri };
        Assert.True((await http.GetAsync("/health/live")).IsSuccessStatusCode);
        Assert.True((await http.GetAsync("/health/ready")).IsSuccessStatusCode);
        var info = await http.GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.True(info.GetProperty("registrationKeyRequired").GetBoolean());
        Assert.Equal(ProtocolInfo.Version, info.GetProperty("protocolVersion").GetInt32());

        var admin = (await http.GetAsync("/api/v1/admin/devices")).StatusCode;
        Assert.True(admin is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound, $"admin API answered {admin} without a key");
    }

    [Fact]
    public async Task Wrong_registration_key_is_rejected()
    {
        await using var client = NewClient(HostKey.Value, "Intruder", key: "wrong-key");
        await Wait.UntilAsync(() => client.Status == SignalingStatus.Rejected, "rejection");
        Assert.Equal(RegisterError.RegistrationKeyInvalid, client.LastRegisterError);
    }

    [Fact]
    public async Task Session_through_relay_with_end_to_end_tls()
    {
        // Host side: registered, and accepts relay offers.
        var injector = new RecordingInjector();
        var ui = ScriptedInteraction.Always(ApprovalDecision.Accept);
        await using var hostAgent = new HostAgent(HostKey.Value, () => new HostOptions { Alias = "Relay host" }, ui, null, new ListLogger("host", _log),
            () => new SyntheticCapturer(), () => injector);
        await using var hostSignal = NewClient(HostKey.Value, "Relay host");
        hostSignal.RelayOfferHandler = async offer =>
        {
            _log.Enqueue($"{DateTime.Now:HH:mm:ss.fff} test: relay offer for session {offer.SessionId}");
            try
            {
                await hostAgent.AcceptRelayAsync(hostSignal.RelayUri(offer.SessionId), offer);
                _log.Enqueue($"{DateTime.Now:HH:mm:ss.fff} test: relay session finished");
            }
            catch (Exception ex)
            {
                _log.Enqueue($"{DateTime.Now:HH:mm:ss.fff} test: relay accept failed {ex}");
            }
        };

        // Controller side: registered, no LAN discovery, no direct endpoints → must use the relay.
        await using var controllerSignal = NewClient(ControllerKey.Value, "Relay controller");
        await Wait.UntilAsync(() => hostSignal.IsOnline && controllerSignal.IsOnline, "both devices online");

        var resolved = await controllerSignal.ResolveAsync(HostKey.Value.DeviceId, default);
        Assert.True(resolved.Found && resolved.Online);
        Assert.Equal(HostKey.Value.Fingerprint, resolved.Fingerprint);

        var known = new MemoryKnownHosts();
        var connector = new Connector(ControllerKey.Value, controllerSignal, null, known, new ListLogger("connector", _log));
        Assert.True(ConnectTargetParser.TryParse(HostKey.Value.DeviceId, 47070, out var target, out _));
        var progress = new List<string>();
        var conn = await connector.ConnectAsync(target, _ => throw new InvalidOperationException("server-pinned; no prompt"),
            new SyncProgress(progress), default);
        Assert.Equal(ConnectionPath.Relay, conn.Path);
        Assert.Equal(HostKey.Value.DeviceId, conn.RemoteDeviceId);
        Assert.Contains(progress, p => p.Contains("relay", StringComparison.OrdinalIgnoreCase));

        var hello = new HelloMessage(ProtocolInfo.Version, ControllerKey.Value.DeviceId, "Relay controller", "Test", "1.0",
            SessionPermissions.ViewScreen | SessionPermissions.ControlInput);
        var session = await ControllerSession.StartAsync(conn, hello, null, Wait.Log);
        await Wait.UntilAsync(() => session.State == ControllerState.Connected, "approval over relay");
        await Wait.UntilAsync(() => session.Framebuffer.Version >= 2, "frames over relay");

        session.SendKey(0x2C, true);
        await Wait.UntilAsync(() => injector.Events.Contains("key 2C down"), "input over relay");

        var request = Assert.Single(ui.Requests);
        Assert.Equal(ConnectionPath.Relay, request.Path);
        await session.DisconnectAsync();
        await ui.Ended.Task.WaitAsync(Wait.Timeout);

        // The audit trail records the relay without any secrets.
        if (_adminKey is null)
        {
            return;
        }

        using var http = new HttpClient { BaseAddress = _baseUri };
        http.DefaultRequestHeaders.Add("X-Admin-Key", _adminKey);
        if (_server is not null)
        {
            await Wait.UntilAsync(() => File.Exists(Path.Combine(_dataDir, "audit", $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl")), "audit file");
        }

        var audit = await http.GetStringAsync($"/api/v1/admin/audit?deviceId={HostKey.Value.DeviceId}");
        Assert.Contains("RelayRequested", audit);
        Assert.DoesNotContain(_key, audit);
        var devices = await http.GetStringAsync("/api/v1/admin/devices");
        Assert.Contains(HostKey.Value.DeviceId, devices);
    }

    [Fact]
    public async Task Resolve_unknown_device_reports_not_found()
    {
        await using var client = NewClient(ControllerKey.Value, "Lookup");
        await Wait.UntilAsync(() => client.IsOnline, "online");
        var r = await client.ResolveAsync("123456789", default);
        Assert.False(r.Found);
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
