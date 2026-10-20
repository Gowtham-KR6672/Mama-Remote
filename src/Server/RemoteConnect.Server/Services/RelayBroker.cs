using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RemoteConnect.Protocol.Signaling;

namespace RemoteConnect.Server.Services;

/// <summary>
/// Pairs the controller's and the host's WebSocket for one session and copies bytes between them.
/// The payload is the peers' own TLS stream — the relay cannot read or alter screen or input data.
/// Each side has its own single-use token; tokens are stored only as hashes.
/// </summary>
public sealed class RelayBroker
{
    private const int BufferSize = 64 * 1024;
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new();
    private readonly ServerOptions _options;
    private readonly AuditLog _audit;
    private readonly ILogger<RelayBroker> _logger;

    public RelayBroker(IOptions<ServerOptions> options, AuditLog audit, ILogger<RelayBroker> logger)
    {
        _options = options.Value;
        _audit = audit;
        _logger = logger;
    }

    public int ActiveCount => _sessions.Count;

    public (string SessionId, string ControllerToken, string HostToken)? Create(string controllerId, string hostId)
    {
        PurgeExpired();
        if (_sessions.Count >= _options.MaxConcurrentRelays)
        {
            return null;
        }

        var id = NewToken(12);
        var controllerToken = NewToken(32);
        var hostToken = NewToken(32);
        _sessions[id] = new RelaySession(id, controllerId, hostId, Hash(controllerToken), Hash(hostToken), DateTimeOffset.UtcNow);
        return (id, controllerToken, hostToken);
    }

    public async Task HandleAsync(HttpContext context, string sessionId)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string token = context.Request.Headers[SignalingContract.RelayTokenHeader].ToString();
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(sessionId, out var session))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var tokenHash = Hash(token);
        bool isHost = CryptographicOperations.FixedTimeEquals(tokenHash, session.HostTokenHash);
        bool isController = !isHost && CryptographicOperations.FixedTimeEquals(tokenHash, session.ControllerTokenHash);
        if (!isHost && !isController)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!session.TryClaim(isHost))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict; // token already used
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        session.Attach(isHost, socket, context.RequestAborted);
        _logger.LogDebug("Relay {Session}: {Side} joined", sessionId, isHost ? "host" : "controller");

        var timeout = TimeSpan.FromSeconds(Math.Max(5, _options.RelayPairTimeoutSeconds));
        var paired = await Task.WhenAny(session.Paired.Task, Task.Delay(timeout, context.RequestAborted)).ConfigureAwait(false) == session.Paired.Task;
        if (!paired)
        {
            if (_sessions.TryRemove(sessionId, out _))
            {
                _audit.Write("RelayExpired", session.ControllerId, session.HostId, sessionId, detail: "peer did not join");
            }

            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation, "peer did not join").ConfigureAwait(false);
            return;
        }

        if (isHost)
        {
            _audit.Write("RelayStarted", session.ControllerId, session.HostId, sessionId);
        }

        var other = isHost ? session.ControllerSocket! : session.HostSocket!;
        var ct = session.Cancellation.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                long total = session.AddBytes(result.Count);
                if (_options.MaxRelayBytesPerSession > 0 && total > _options.MaxRelayBytesPerSession)
                {
                    _logger.LogWarning("Relay {Session} exceeded byte limit", sessionId);
                    break;
                }

                await other.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, result.EndOfMessage, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            session.Cancellation.Cancel(); // stop the opposite direction too
            if (_sessions.TryRemove(sessionId, out _))
            {
                _audit.Write("RelayEnded", session.ControllerId, session.HostId, sessionId,
                    detail: $"bytes={session.Bytes} duration={(DateTimeOffset.UtcNow - session.CreatedAt).TotalSeconds:F0}s");
            }

            await CloseQuietly(socket, WebSocketCloseStatus.NormalClosure, "relay closed").ConfigureAwait(false);
        }
    }

    private void PurgeExpired()
    {
        var limit = TimeSpan.FromSeconds(Math.Max(5, _options.RelayPairTimeoutSeconds) * 2);
        foreach (var (id, s) in _sessions)
        {
            if (!s.Paired.Task.IsCompleted && DateTimeOffset.UtcNow - s.CreatedAt > limit)
            {
                _sessions.TryRemove(id, out _);
            }
        }
    }

    private static async Task CloseQuietly(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(status, reason, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string NewToken(int bytes)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    private sealed class RelaySession(string id, string controllerId, string hostId, byte[] controllerTokenHash, byte[] hostTokenHash, DateTimeOffset createdAt)
    {
        private int _hostClaimed;
        private int _controllerClaimed;
        private long _bytes;

        public string Id { get; } = id;

        public string ControllerId { get; } = controllerId;

        public string HostId { get; } = hostId;

        public byte[] ControllerTokenHash { get; } = controllerTokenHash;

        public byte[] HostTokenHash { get; } = hostTokenHash;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public WebSocket? HostSocket { get; private set; }

        public WebSocket? ControllerSocket { get; private set; }

        public TaskCompletionSource Paired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Cancellation { get; } = new();

        public long Bytes => Interlocked.Read(ref _bytes);

        public long AddBytes(int n) => Interlocked.Add(ref _bytes, n);

        public bool TryClaim(bool host)
            => Interlocked.Exchange(ref host ? ref _hostClaimed : ref _controllerClaimed, 1) == 0;

        public void Attach(bool host, WebSocket socket, CancellationToken requestAborted)
        {
            requestAborted.Register(() => Cancellation.Cancel());
            lock (this)
            {
                if (host)
                {
                    HostSocket = socket;
                }
                else
                {
                    ControllerSocket = socket;
                }

                if (HostSocket is not null && ControllerSocket is not null)
                {
                    Paired.TrySetResult();
                }
            }
        }
    }
}
