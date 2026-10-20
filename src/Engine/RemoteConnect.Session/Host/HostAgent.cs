using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RemoteConnect.Platform;
using RemoteConnect.Protocol;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.Session.Host;

/// <summary>
/// Accepts incoming connections (direct LAN listener and server relay), authenticates the controller,
/// asks the local user (or checks the opt-in unattended password) and runs approved sessions.
/// </summary>
public sealed class HostAgent : IAsyncDisposable
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);

    private readonly DeviceIdentity _identity;
    private readonly Func<HostOptions> _options;
    private readonly IHostInteraction _interaction;
    private readonly IClipboardBridge? _clipboard;
    private readonly ILogger _logger;
    private readonly Func<IScreenCapturer> _capturerFactory;
    private readonly Func<IInputInjector> _injectorFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<HostSession> _sessions = new();
    private readonly object _sync = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private int _pendingRequests;

    public HostAgent(
        DeviceIdentity identity,
        Func<HostOptions> options,
        IHostInteraction interaction,
        IClipboardBridge? clipboard,
        ILogger logger,
        Func<IScreenCapturer>? capturerFactory = null,
        Func<IInputInjector>? injectorFactory = null)
    {
        _identity = identity;
        _options = options;
        _interaction = interaction;
        _clipboard = clipboard;
        _logger = logger;
        _capturerFactory = capturerFactory ?? PlatformServices.CreateCapturer;
        _injectorFactory = injectorFactory ?? PlatformServices.CreateInjector;
    }

    public UnattendedGuard Guard { get; } = new();

    public int? ListeningPort { get; private set; }

    public string? ListenerError { get; private set; }

    public event Action? SessionsChanged;

    public IReadOnlyList<HostSession> Sessions
    {
        get
        {
            lock (_sync)
            {
                return _sessions.ToArray();
            }
        }
    }

    /// <summary>Listens for direct connections. Binds dual-stack on all interfaces (or loopback only, for tests).</summary>
    public void StartLan(int port, bool loopbackOnly = false)
    {
        StopLan();
        try
        {
            TcpListener listener;
            if (loopbackOnly)
            {
                listener = new TcpListener(IPAddress.Loopback, port);
            }
            else
            {
                listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
            }

            listener.Start(backlog: 16);
            _listener = listener;
            _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            ListenerError = null;
            _ = AcceptLoopAsync(listener, _listenerCts.Token);
            _logger.LogInformation("Listening for direct connections on port {Port}", ListeningPort);
        }
        catch (SocketException ex)
        {
            ListeningPort = null;
            ListenerError = $"Port {port} unavailable: {ex.Message}";
            _logger.LogWarning("Could not listen on port {Port}: {Message}", port, ex.Message);
        }
    }

    public void StopLan()
    {
        _listenerCts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _listenerCts = null;
        ListeningPort = null;
    }

    /// <summary>The server announced a controller; join the relay as TLS server and expect exactly that controller.</summary>
    public async Task AcceptRelayAsync(Uri relayUri, RelayOffer offer)
    {
        try
        {
            var conn = await PeerConnector.ConnectRelayAsync(
                _identity, relayUri, offer.Token, offer.ControllerFingerprint, asTlsServer: true, _cts.Token).ConfigureAwait(false);
            if (conn.RemoteDeviceId != offer.ControllerId)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                _logger.LogWarning("Relay peer identity does not match the offer; dropped.");
                return;
            }

            await HandleConnectionAsync(conn).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Relay connection for session {Session} failed: {Message}", offer.SessionId, ex.Message);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning("Accept failed: {Message}", ex.Message);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    client.NoDelay = true;
                    string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    var conn = await PeerConnector.ServerHandshakeAsync(_identity, client.GetStream(), client, endpoint, ConnectionPath.Direct, ct).ConfigureAwait(false);
                    await HandleConnectionAsync(conn).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    client.Dispose();
                    _logger.LogInformation("Incoming connection rejected during handshake: {Message}", ex.Message);
                }
            }, CancellationToken.None);
        }
    }

    internal async Task HandleConnectionAsync(PeerConnection conn)
    {
        bool handedOff = false;
        try
        {
            conn.StartReceiving();
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            helloTimeout.CancelAfter(HelloTimeout);
            var first = await conn.ReceiveAsync(helloTimeout.Token).ConfigureAwait(false);
            if (first is not { Type: MessageType.Hello } helloFrame)
            {
                return;
            }

            var hello = MessageFramer.ParseJson<HelloMessage>(helloFrame);
            var options = _options();

            if (hello.ProtocolVersion != ProtocolInfo.Version)
            {
                await RefuseAsync(conn, options, HelloOutcome.ProtocolMismatch, $"Protocol version {hello.ProtocolVersion} is not supported (expected {ProtocolInfo.Version}).").ConfigureAwait(false);
                return;
            }

            // The claimed ID must be the one bound to the TLS key the controller just proved it holds.
            if (!DeviceIds.TryNormalize(hello.DeviceId, out var claimedId) || claimedId != conn.RemoteDeviceId)
            {
                await RefuseAsync(conn, options, HelloOutcome.IdentityMismatch, "Device ID does not match the presented key.").ConfigureAwait(false);
                return;
            }

            if (!options.AllowIncoming || !PlatformServices.CanHost)
            {
                await RefuseAsync(conn, options, HelloOutcome.NotAllowed, "This device does not accept incoming connections.").ConfigureAwait(false);
                return;
            }

            if (!options.AttendedApprovalAvailable && !options.IsUnattendedAvailableFor(conn.RemoteDeviceId))
            {
                await RefuseAsync(conn, options, HelloOutcome.NotAllowed,
                    "Nobody can accept requests on this computer right now, and unattended access is not set up for your device.").ConfigureAwait(false);
                return;
            }

            if (Sessions.Count + Volatile.Read(ref _pendingRequests) >= options.MaxSessions)
            {
                await RefuseAsync(conn, options, HelloOutcome.Busy, "The remote device is busy with another session.").ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref _pendingRequests);
            (ApprovalDecision? decision, bool unattended, HelloOutcome outcome) result;
            try
            {
                result = await AwaitDecisionAsync(conn, hello, options).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingRequests);
            }

            if (result.decision is null or ApprovalDecision.Reject)
            {
                if (!conn.IsClosed)
                {
                    await RefuseAsync(conn, options, result.outcome,
                        result.outcome == HelloOutcome.TimedOut ? "The remote user did not respond." : "The remote user declined the request.").ConfigureAwait(false);
                }

                _logger.LogInformation("Request from {Controller} refused ({Outcome})", conn.RemoteDeviceId, result.outcome);
                return;
            }

            var granted = result.unattended
                ? options.UnattendedPermissions & (hello.RequestedPermissions | SessionPermissions.ViewScreen)
                : result.decision == ApprovalDecision.ViewOnly
                    ? SessionPermissions.ViewScreen
                    : options.AttendedPermissions & (hello.RequestedPermissions | SessionPermissions.ViewScreen);
            granted |= SessionPermissions.ViewScreen;
            granted &= ~SessionPermissions.FileTransfer; // not implemented yet; never advertise it

            await conn.SendJsonAsync(MessageType.HelloResult, new HelloResultMessage(
                HelloOutcome.Accepted, granted, options.Alias, options.Platform, null, false), _cts.Token).ConfigureAwait(false);

            var session = new HostSession(conn, hello, granted, result.unattended, options.DefaultQuality,
                _capturerFactory, _injectorFactory, _clipboard, _logger);
            handedOff = true;
            await RunSessionAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ProtocolException or IOException)
        {
            _logger.LogInformation("Incoming request ended: {Message}", ex.Message);
        }
        finally
        {
            if (!handedOff)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<(ApprovalDecision? Decision, bool Unattended, HelloOutcome Outcome)> AwaitDecisionAsync(
        PeerConnection conn, HelloMessage hello, HostOptions options)
    {
        string controllerId = conn.RemoteDeviceId;
        bool unattendedAvailable = options.IsUnattendedAvailableFor(controllerId);

        bool passwordOnly = !options.AttendedApprovalAvailable;
        await conn.SendJsonAsync(MessageType.HelloResult, new HelloResultMessage(
            HelloOutcome.PendingApproval, SessionPermissions.None, options.Alias, options.Platform,
            passwordOnly ? "Enter the unattended-access password for this computer." : "Waiting for the remote user to accept.",
            unattendedAvailable, passwordOnly), _cts.Token).ConfigureAwait(false);

        using var approvalCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        approvalCts.CancelAfter(options.ApprovalTimeout);
        var request = new IncomingRequest(
            controllerId,
            string.IsNullOrWhiteSpace(hello.Alias) ? "Unknown device" : hello.Alias,
            hello.Platform,
            conn.RemoteFingerprint,
            conn.RemoteEndpoint,
            conn.Path,
            hello.RequestedPermissions,
            DateTimeOffset.UtcNow + options.ApprovalTimeout,
            unattendedAvailable);

        var approvalTask = passwordOnly
            ? NoPromptAsync(approvalCts.Token)
            : _interaction.RequestApprovalAsync(request, approvalCts.Token);
        var timeoutTask = Task.Delay(Timeout.Infinite, approvalCts.Token);
        try
        {
            while (true)
            {
                var messageTask = conn.WaitToReadAsync(approvalCts.Token).AsTask();
                var done = await Task.WhenAny(approvalTask, messageTask, timeoutTask).ConfigureAwait(false);

                if (done == approvalTask && approvalTask.IsCompletedSuccessfully && !approvalCts.IsCancellationRequested)
                {
                    var d = approvalTask.Result;
                    return (d, false, d == ApprovalDecision.Reject ? HelloOutcome.Rejected : HelloOutcome.Accepted);
                }

                if (done == approvalTask || approvalCts.IsCancellationRequested)
                {
                    return (null, false, HelloOutcome.TimedOut);
                }

                if (!messageTask.IsCompletedSuccessfully || !messageTask.Result)
                {
                    return (null, false, HelloOutcome.Rejected); // controller went away
                }

                while (conn.TryRead(out var frame))
                {
                    if (frame.Type == MessageType.Disconnect)
                    {
                        return (null, false, HelloOutcome.Rejected);
                    }

                    if (frame.Type != MessageType.PasswordAuth)
                    {
                        continue; // nothing else is accepted before approval
                    }

                    var auth = MessageFramer.ParseJson<PasswordAuthMessage>(frame);
                    if (!unattendedAvailable)
                    {
                        await SendAuthStatusAsync(conn, new AuthStatusMessage(false, "Unattended access is not enabled for your device.", 0, false)).ConfigureAwait(false);
                    }
                    else if (Guard.IsLockedOut(controllerId))
                    {
                        await SendAuthStatusAsync(conn, new AuthStatusMessage(false, "Too many failed attempts. Try again later.", 0, true)).ConfigureAwait(false);
                    }
                    else if (PasswordHasher.Verify(auth.Password, options.UnattendedPasswordHash))
                    {
                        Guard.RecordSuccess(controllerId);
                        await SendAuthStatusAsync(conn, new AuthStatusMessage(true, "Password accepted.", 0, false)).ConfigureAwait(false);
                        _logger.LogInformation("Unattended access granted to {Controller}", controllerId);
                        return (ApprovalDecision.Accept, true, HelloOutcome.Accepted);
                    }
                    else
                    {
                        var (remaining, locked) = Guard.RecordFailure(controllerId);
                        _logger.LogWarning("Wrong unattended password from {Controller} ({Remaining} attempts left)", controllerId, remaining);
                        await SendAuthStatusAsync(conn, new AuthStatusMessage(false, locked ? "Too many failed attempts. Try again later." : "Wrong password.", remaining, locked)).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            approvalCts.Cancel(); // closes the prompt if it is still showing
            try
            {
                await approvalTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Service mode: there is no one to ask, so the "prompt" only ends when the request times out.</summary>
    private static async Task<ApprovalDecision> NoPromptAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return ApprovalDecision.Reject;
    }

    private Task SendAuthStatusAsync(PeerConnection conn, AuthStatusMessage status)
        => conn.SendJsonAsync(MessageType.AuthStatus, status, _cts.Token);

    private async Task RefuseAsync(PeerConnection conn, HostOptions options, HelloOutcome outcome, string message)
    {
        try
        {
            await conn.SendJsonAsync(MessageType.HelloResult, new HelloResultMessage(
                outcome, SessionPermissions.None, options.Alias, options.Platform, message, false), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task RunSessionAsync(HostSession session)
    {
        lock (_sync)
        {
            _sessions.Add(session);
        }

        SessionsChanged?.Invoke();
        _logger.LogInformation(
            "Session {Session} started: controller {Controller} ({Alias}) via {Path}, permissions {Permissions}, unattended={Unattended}",
            session.Id, session.ControllerId, session.ControllerAlias, session.Path, session.Granted, session.IsUnattended);
        SafeInvoke(() => _interaction.OnSessionStarted(session));

        (DisconnectReason Reason, string? Detail) end = (DisconnectReason.HostDisconnected, null);
        try
        {
            end = await session.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            end = (DisconnectReason.HostDisconnected, ex.Message);
            _logger.LogWarning(ex, "Session {Session} crashed", session.Id);
        }
        finally
        {
            lock (_sync)
            {
                _sessions.Remove(session);
            }

            _logger.LogInformation("Session {Session} ended: {Reason} {Detail}", session.Id, end.Reason, end.Detail);
            SafeInvoke(() => _interaction.OnSessionEnded(session, end.Reason, end.Detail));
            SessionsChanged?.Invoke();
        }
    }

    private void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Host UI callback failed");
        }
    }

    public void EndAllSessions(DisconnectReason reason)
    {
        foreach (var s in Sessions)
        {
            s.End(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        EndAllSessions(DisconnectReason.HostShuttingDown);
        StopLan();
        await Task.Delay(200).ConfigureAwait(false);
        _cts.Cancel();
        _cts.Dispose();
    }
}
