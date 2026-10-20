using RemoteConnect.Protocol;
using RemoteConnect.Security;
using RemoteConnect.Session.Controller;
using RemoteConnect.Session.Host;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.IntegrationTests;

/// <summary>
/// Full sessions over real TCP + mutual TLS on 127.0.0.1: handshake, approval, screen frames, input, permissions,
/// disconnect cleanup. Only the screen source and the input sink are test doubles.
/// </summary>
public class LoopbackSessionTests : IAsyncLifetime
{
    private static readonly Lazy<DeviceIdentity> HostKey = new(DeviceIdentity.Create);
    private static readonly Lazy<DeviceIdentity> ControllerKey = new(DeviceIdentity.Create);

    private readonly RecordingInjector _injector = new();
    private HostAgent? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private HostAgent StartHost(IHostInteraction interaction, HostOptions? options = null)
    {
        options ??= new HostOptions { Alias = "Test host", Platform = "Test" };
        _host = new HostAgent(HostKey.Value, () => options, interaction, null, Wait.Log, () => new SyntheticCapturer(), () => _injector);
        _host.StartLan(0, loopbackOnly: true);
        Assert.NotNull(_host.ListeningPort);
        return _host;
    }

    private async Task<ControllerSession> ConnectAsync(string? expectedFingerprint)
    {
        var conn = await PeerConnector.ConnectDirectAsync(ControllerKey.Value, "127.0.0.1", _host!.ListeningPort!.Value, expectedFingerprint, TimeSpan.FromSeconds(5), default);
        var hello = new HelloMessage(ProtocolInfo.Version, ControllerKey.Value.DeviceId, "Test controller", "Test", "1.0",
            SessionPermissions.ViewScreen | SessionPermissions.ControlInput | SessionPermissions.Clipboard);
        return await ControllerSession.StartAsync(conn, hello, null, Wait.Log);
    }

    [Fact]
    public async Task Approved_session_streams_frames_and_applies_input_then_cleans_up()
    {
        var ui = ScriptedInteraction.Always(ApprovalDecision.Accept);
        StartHost(ui);
        var session = await ConnectAsync(HostKey.Value.Fingerprint);

        await Wait.UntilAsync(() => session.State == ControllerState.Connected, "approval");
        Assert.True(session.Has(SessionPermissions.ControlInput));
        Assert.Equal(HostKey.Value.DeviceId, session.RemoteDeviceId);
        Assert.Equal(ConnectionPath.Direct, session.Path);

        var request = Assert.Single(ui.Requests);
        Assert.Equal(ControllerKey.Value.DeviceId, request.DeviceId); // host saw the TLS-proven identity

        await Wait.UntilAsync(() => session.Framebuffer.Version >= 3, "three screen updates");
        Assert.Equal(640, session.Framebuffer.Width);
        Assert.Equal(360, session.Framebuffer.Height);
        Assert.Single(session.Monitors);

        session.SendPointer(PointerAction.Move, MouseButton.None, 0.5, 0.5);
        session.SendPointer(PointerAction.Down, MouseButton.Left, 0.25, 0.75);
        session.SendPointer(PointerAction.Up, MouseButton.Left, 0.25, 0.75);
        session.SendKey(0x04, true);
        session.SendKey(0x04, false);
        session.SendScroll(0.5, 0.5, 0, 120);
        await Wait.UntilAsync(() => _injector.Events.Count >= 6, "input to arrive");
        var events = _injector.Events.ToArray();
        Assert.Contains("move 0.50,0.50", events);
        Assert.Contains("Left down 0.25,0.75", events);
        Assert.Contains("key 04 down", events);
        Assert.Contains("scroll 0,120", events);

        var hostSession = await ui.Started.Task.WaitAsync(Wait.Timeout);
        await session.DisconnectAsync();
        var reason = await ui.Ended.Task.WaitAsync(Wait.Timeout);
        Assert.Equal(DisconnectReason.ControllerDisconnected, reason);
        Assert.True(_injector.ReleaseAllCalls >= 1, "stuck keys must be released on disconnect");
        Assert.Empty(_host!.Sessions);
        Assert.True(hostSession.FramesSent >= 3);
    }

    [Fact]
    public async Task View_only_session_never_injects_input()
    {
        StartHost(ScriptedInteraction.Always(ApprovalDecision.ViewOnly));
        var session = await ConnectAsync(HostKey.Value.Fingerprint);
        await Wait.UntilAsync(() => session.State == ControllerState.Connected, "approval");
        Assert.False(session.Has(SessionPermissions.ControlInput));
        await Wait.UntilAsync(() => session.Framebuffer.Version >= 1, "frame");

        session.SendKey(0x04, true);
        session.SendPointer(PointerAction.Down, MouseButton.Left, 0.1, 0.1);
        await Task.Delay(300);
        Assert.Empty(_injector.Events);
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task Host_can_revoke_control_mid_session()
    {
        var ui = ScriptedInteraction.Always(ApprovalDecision.Accept);
        StartHost(ui);
        var session = await ConnectAsync(HostKey.Value.Fingerprint);
        var hostSession = await ui.Started.Task.WaitAsync(Wait.Timeout);

        await hostSession.SetPermissionsAsync(SessionPermissions.ViewScreen);
        await Wait.UntilAsync(() => !session.Has(SessionPermissions.ControlInput), "permission change to reach controller");
        Assert.True(_injector.ReleaseAllCalls >= 1);

        hostSession.End(DisconnectReason.Revoked);
        await Wait.UntilAsync(() => session.State == ControllerState.Ended, "controller to see the revocation");
        Assert.Equal(DisconnectReason.Revoked, session.EndReason);
    }

    [Fact]
    public async Task Rejected_request_ends_without_frames()
    {
        StartHost(ScriptedInteraction.Always(ApprovalDecision.Reject));
        var session = await ConnectAsync(HostKey.Value.Fingerprint);
        await Wait.UntilAsync(() => session.State == ControllerState.Ended, "rejection");
        Assert.Equal(HelloOutcome.Rejected, session.Outcome);
        Assert.Equal(0, session.Framebuffer.Version);
    }

    [Fact]
    public async Task Wrong_pinned_fingerprint_is_refused_before_any_data()
    {
        StartHost(ScriptedInteraction.Always(ApprovalDecision.Accept));
        await Assert.ThrowsAsync<HostIdentityMismatchException>(() => ConnectAsync(ControllerKey.Value.Fingerprint));
    }

    [Fact]
    public async Task Unattended_password_wrong_then_right()
    {
        var options = new HostOptions
        {
            Alias = "Unattended host",
            UnattendedEnabled = true,
            UnattendedPasswordHash = PasswordHasher.Hash("s3cret-pass"),
            UnattendedPermissions = SessionPermissions.ViewScreen | SessionPermissions.ControlInput,
        };
        StartHost(ScriptedInteraction.NeverAnswers(), options);
        var session = await ConnectAsync(HostKey.Value.Fingerprint);
        await Wait.UntilAsync(() => session.State == ControllerState.WaitingForApproval && session.UnattendedAvailable, "pending state");

        var statuses = new System.Collections.Concurrent.ConcurrentQueue<AuthStatusMessage>();
        session.AuthStatusReceived += statuses.Enqueue;
        session.SendPassword("wrong-password");
        await Wait.UntilAsync(() => !statuses.IsEmpty, "auth failure");
        Assert.False(statuses.First().Success);
        Assert.Equal(UnattendedGuard.MaxFailuresPerDevice - 1, statuses.First().AttemptsRemaining);

        session.SendPassword("s3cret-pass");
        await Wait.UntilAsync(() => session.State == ControllerState.Connected, "unattended login");
        Assert.True(session.Has(SessionPermissions.ControlInput));
        Assert.False(session.Has(SessionPermissions.Clipboard)); // limited to the unattended policy
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task Unattended_password_is_refused_when_feature_is_off()
    {
        StartHost(ScriptedInteraction.NeverAnswers(), new HostOptions { ApprovalTimeout = TimeSpan.FromSeconds(3) });
        var session = await ConnectAsync(HostKey.Value.Fingerprint);
        await Wait.UntilAsync(() => session.State == ControllerState.WaitingForApproval, "pending");
        Assert.False(session.UnattendedAvailable);

        var statuses = new System.Collections.Concurrent.ConcurrentQueue<AuthStatusMessage>();
        session.AuthStatusReceived += statuses.Enqueue;
        session.SendPassword("anything-at-all");
        await Wait.UntilAsync(() => !statuses.IsEmpty, "refusal");
        Assert.False(statuses.First().Success);

        await Wait.UntilAsync(() => session.State == ControllerState.Ended, "approval timeout");
        Assert.Equal(HelloOutcome.TimedOut, session.Outcome);
    }

    [Fact]
    public async Task Second_controller_gets_busy_while_a_session_is_active()
    {
        var ui = ScriptedInteraction.Always(ApprovalDecision.Accept);
        StartHost(ui);
        var first = await ConnectAsync(HostKey.Value.Fingerprint);
        await Wait.UntilAsync(() => first.State == ControllerState.Connected, "first session");

        var second = await ConnectAsync(HostKey.Value.Fingerprint);
        await Wait.UntilAsync(() => second.State == ControllerState.Ended, "busy refusal");
        Assert.Equal(HelloOutcome.Busy, second.Outcome);
        await first.DisconnectAsync();
    }

    [Fact]
    public async Task Connector_trust_on_first_use_then_pins()
    {
        StartHost(ScriptedInteraction.Always(ApprovalDecision.Reject));
        var known = new MemoryKnownHosts();
        var connector = new Connector(ControllerKey.Value, null, null, known, Wait.Log);
        Assert.True(ConnectTargetParser.TryParse($"127.0.0.1:{_host!.ListeningPort}", 0, out var target, out _));

        // Declined → nothing remembered.
        await Assert.ThrowsAsync<OperationCanceledException>(() => connector.ConnectAsync(target, _ => Task.FromResult(false), null, default));
        Assert.Null(known.GetFingerprint(target.PinKey));

        // Accepted → pinned, and the ID → key binding is remembered too.
        UnknownHostInfo? seen = null;
        var conn = await connector.ConnectAsync(target, info => { seen = info; return Task.FromResult(true); }, null, default);
        Assert.Equal(HostKey.Value.DeviceId, seen!.DeviceId);
        Assert.Equal(HostKey.Value.Fingerprint, known.GetFingerprint(target.PinKey));
        Assert.Equal(HostKey.Value.Fingerprint, known.GetFingerprint("id:" + HostKey.Value.DeviceId));
        await conn.DisposeAsync();

        // Next time no prompt is needed.
        var again = await connector.ConnectAsync(target, _ => throw new InvalidOperationException("should not prompt"), null, default);
        await again.DisposeAsync();
    }
}
