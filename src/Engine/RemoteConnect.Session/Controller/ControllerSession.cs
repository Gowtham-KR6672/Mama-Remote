using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RemoteConnect.Protocol;
using RemoteConnect.Session.Host;
using RemoteConnect.Session.Media;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.Session.Controller;

public enum ControllerState
{
    WaitingForApproval,
    Connected,
    Ended,
}

public sealed record SessionStats(double Fps, double Kbps, double RttMs, long FramesReceived);

/// <summary>Controller side of a session: renders remote frames and forwards local input.</summary>
public sealed class ControllerSession : IAsyncDisposable
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly PeerConnection _connection;
    private readonly IClipboardBridge? _clipboard;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(MessageType Type, byte[] Payload)> _outbox = Channel.CreateUnbounded<(MessageType, byte[])>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();
    private long _lastActivityTicks = Stopwatch.GetTimestamp();
    private long _framesReceived;
    private long _framesAtLastStats;
    private long _bytesAtLastStats;
    private TimeSpan _lastStatsTime;
    private double _rttMs;
    private string? _lastClipboard;
    private int _ended;

    private ControllerSession(PeerConnection connection, IClipboardBridge? clipboard, ILogger logger)
    {
        _connection = connection;
        _clipboard = clipboard;
        _logger = logger;
    }

    public ControllerState State { get; private set; } = ControllerState.WaitingForApproval;

    public HelloOutcome? Outcome { get; private set; }

    public string? StatusMessage { get; private set; }

    public SessionPermissions Granted { get; private set; }

    public string HostAlias { get; private set; } = string.Empty;

    public string HostPlatform { get; private set; } = string.Empty;

    public bool UnattendedAvailable { get; private set; }

    /// <summary>The host cannot show an approval prompt (service mode): only the unattended password works.</summary>
    public bool PasswordOnly { get; private set; }

    public string RemoteDeviceId => _connection.RemoteDeviceId;

    public string RemoteFingerprint => _connection.RemoteFingerprint;

    public ConnectionPath Path => _connection.Path;

    public string RemoteEndpoint => _connection.RemoteEndpoint;

    public string TlsProtocol => _connection.TlsProtocol;

    public RemoteFramebuffer Framebuffer { get; } = new();

    public IReadOnlyList<RemoteMonitor> Monitors { get; private set; } = Array.Empty<RemoteMonitor>();

    public int ActiveMonitor { get; private set; }

    public DisconnectReason? EndReason { get; private set; }

    public Task Completion => _completion.Task;

    public event Action? StateChanged;

    public event Action? FrameUpdated;

    public event Action? LayoutChanged;

    public event Action? PermissionsChanged;

    public event Action<AuthStatusMessage>? AuthStatusReceived;

    public bool Has(SessionPermissions p) => (Granted & p) == p;

    public static async Task<ControllerSession> StartAsync(PeerConnection connection, HelloMessage hello, IClipboardBridge? clipboard, ILogger logger)
    {
        var session = new ControllerSession(connection, clipboard, logger);
        connection.StartReceiving();
        await connection.SendJsonAsync(MessageType.Hello, hello).ConfigureAwait(false);
        _ = Task.Run(session.ReceiveLoopAsync);
        _ = Task.Run(session.SendLoopAsync);
        _ = Task.Run(session.HousekeepingLoopAsync);
        return session;
    }

    public SessionStats GetStats()
    {
        var now = _statsClock.Elapsed;
        double seconds = Math.Max(0.001, (now - _lastStatsTime).TotalSeconds);
        long frames = Interlocked.Read(ref _framesReceived);
        long bytes = _connection.BytesReceived;
        var stats = new SessionStats(
            (frames - _framesAtLastStats) / seconds,
            (bytes - _bytesAtLastStats) * 8 / 1000.0 / seconds,
            _rttMs,
            frames);
        _lastStatsTime = now;
        _framesAtLastStats = frames;
        _bytesAtLastStats = bytes;
        return stats;
    }

    // ---- Input (normalized 0..1 coordinates over the remote monitor) ----

    public void SendPointer(PointerAction action, MouseButton button, double nx, double ny)
    {
        if (State != ControllerState.Connected || !Has(SessionPermissions.ControlInput))
        {
            return;
        }

        Enqueue(MessageType.Pointer, new PointerMessage(action, button, ToWire(nx), ToWire(ny)).Encode());
    }

    public void SendScroll(double nx, double ny, int deltaX, int deltaY)
    {
        if (State != ControllerState.Connected || !Has(SessionPermissions.ControlInput))
        {
            return;
        }

        Enqueue(MessageType.Scroll, new ScrollMessage(ToWire(nx), ToWire(ny),
            (short)Math.Clamp(deltaX, short.MinValue, short.MaxValue),
            (short)Math.Clamp(deltaY, short.MinValue, short.MaxValue)).Encode());
    }

    public void SendKey(ushort hidUsage, bool down)
    {
        if (State != ControllerState.Connected || !Has(SessionPermissions.ControlInput))
        {
            return;
        }

        Enqueue(MessageType.Key, new KeyMessage(hidUsage, down).Encode());
    }

    public void SelectMonitor(int index) => Enqueue(MessageType.SelectMonitor, MessageFramer.SerializeJson(new SelectMonitorMessage(index)));

    public void SetQuality(QualityPreset preset) => Enqueue(MessageType.SetQuality, MessageFramer.SerializeJson(new SetQualityMessage(preset)));

    public void SendPassword(string password) => Enqueue(MessageType.PasswordAuth, MessageFramer.SerializeJson(new PasswordAuthMessage(password)));

    public async Task DisconnectAsync()
    {
        await _connection.TrySendAsync(MessageType.Disconnect,
            MessageFramer.SerializeJson(new DisconnectMessage(DisconnectReason.ControllerDisconnected, null)),
            TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await EndAsync(DisconnectReason.ControllerDisconnected, "You ended the session.").ConfigureAwait(false);
    }

    private static ushort ToWire(double n) => (ushort)Math.Round(Math.Clamp(n, 0, 1) * 65535);

    private void Enqueue(MessageType type, byte[] payload) => _outbox.Writer.TryWrite((type, payload));

    private async Task SendLoopAsync()
    {
        var reader = _outbox.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    // Coalesce bursts of pointer moves: only the latest position matters.
                    while (IsMove(item) && reader.TryPeek(out var next) && IsMove(next))
                    {
                        reader.TryRead(out item);
                    }

                    await _connection.SendAsync(item.Type, item.Payload, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await EndAsync(DisconnectReason.HostDisconnected, ex.Message).ConfigureAwait(false);
        }
    }

    private static bool IsMove((MessageType Type, byte[] Payload) item)
        => item.Type == MessageType.Pointer && item.Payload.Length > 0 && item.Payload[0] == (byte)PointerAction.Move;

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await _connection.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    await EndAsync(DisconnectReason.HostDisconnected,
                        StatusMessage ?? _connection.Error?.Message ?? "The connection was closed.").ConfigureAwait(false);
                    return;
                }

                Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());
                await HandleAsync(frame.Value).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ProtocolException ex)
        {
            _logger.LogWarning("Protocol error from host: {Message}", ex.Message);
            await EndAsync(DisconnectReason.ProtocolError, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(Frame frame)
    {
        switch (frame.Type)
        {
            case MessageType.HelloResult:
            {
                var m = MessageFramer.ParseJson<HelloResultMessage>(frame);
                HostAlias = m.HostAlias;
                HostPlatform = m.HostPlatform;
                Outcome = m.Outcome;
                StatusMessage = m.Message;
                if (m.Outcome == HelloOutcome.PendingApproval)
                {
                    UnattendedAvailable = m.UnattendedAvailable;
                    PasswordOnly = m.PasswordOnly;
                    State = ControllerState.WaitingForApproval;
                    StateChanged?.Invoke();
                }
                else if (m.Outcome == HelloOutcome.Accepted)
                {
                    Granted = m.Granted;
                    State = ControllerState.Connected;
                    StatusMessage = null;
                    _lastClipboard = await SafeGetClipboardAsync().ConfigureAwait(false);
                    StateChanged?.Invoke();
                }
                else
                {
                    await EndAsync(DisconnectReason.HostDisconnected, m.Message ?? m.Outcome.ToString()).ConfigureAwait(false);
                }

                break;
            }

            case MessageType.AuthStatus:
                AuthStatusReceived?.Invoke(MessageFramer.ParseJson<AuthStatusMessage>(frame));
                break;

            case MessageType.ScreenLayout:
            {
                var m = MessageFramer.ParseJson<ScreenLayoutMessage>(frame);
                Monitors = m.Monitors;
                ActiveMonitor = m.ActiveMonitor;
                LayoutChanged?.Invoke();
                break;
            }

            case MessageType.FrameUpdate:
            {
                if (State != ControllerState.Connected)
                {
                    break;
                }

                var update = FrameUpdate.Decode(frame.Memory);
                Framebuffer.Apply(update);
                Interlocked.Increment(ref _framesReceived);
                // Ack after decoding: the host never runs more than two frames ahead of what we can display.
                await _connection.SendAsync(MessageType.FrameAck, SimpleMessages.EncodeUInt32(update.Sequence), _cts.Token).ConfigureAwait(false);
                FrameUpdated?.Invoke();
                break;
            }

            case MessageType.PermissionsChanged:
                Granted = MessageFramer.ParseJson<PermissionsChangedMessage>(frame).Granted;
                PermissionsChanged?.Invoke();
                break;

            case MessageType.ClipboardText:
            {
                var m = MessageFramer.ParseJson<ClipboardTextMessage>(frame);
                if (_clipboard is not null && Has(SessionPermissions.Clipboard) && m.Text.Length <= ClipboardTextMessage.MaxChars)
                {
                    _lastClipboard = m.Text;
                    await _clipboard.SetTextAsync(m.Text).ConfigureAwait(false);
                }

                break;
            }

            case MessageType.Ping:
                await _connection.SendAsync(MessageType.Pong, frame.Memory, _cts.Token).ConfigureAwait(false);
                break;

            case MessageType.Pong:
            {
                long sent = SimpleMessages.DecodeInt64(frame.Span);
                _rttMs = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                break;
            }

            case MessageType.Disconnect:
            {
                var m = MessageFramer.ParseJson<DisconnectMessage>(frame);
                string text = m.Reason switch
                {
                    DisconnectReason.Revoked => "The remote user ended the session.",
                    DisconnectReason.HostShuttingDown => "The remote device is shutting down RemoteConnect.",
                    DisconnectReason.Timeout => "The session timed out.",
                    _ => m.Message ?? "The remote device ended the session.",
                };
                await EndAsync(m.Reason, text).ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task HousekeepingLoopAsync()
    {
        var sincePing = Stopwatch.StartNew();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
                if (State != ControllerState.Connected)
                {
                    continue;
                }

                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivityTicks)) > IdleTimeout)
                {
                    await EndAsync(DisconnectReason.Timeout, "The remote device stopped responding.").ConfigureAwait(false);
                    return;
                }

                if (sincePing.Elapsed >= PingInterval)
                {
                    sincePing.Restart();
                    Enqueue(MessageType.Ping, SimpleMessages.EncodeInt64(Stopwatch.GetTimestamp()));
                }

                if (_clipboard is not null && Has(SessionPermissions.Clipboard))
                {
                    var text = await SafeGetClipboardAsync().ConfigureAwait(false);
                    if (text is not null && text != _lastClipboard && text.Length <= ClipboardTextMessage.MaxChars)
                    {
                        _lastClipboard = text;
                        Enqueue(MessageType.ClipboardText, MessageFramer.SerializeJson(new ClipboardTextMessage(text)));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string?> SafeGetClipboardAsync()
    {
        try
        {
            return _clipboard is null ? null : await _clipboard.GetTextAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task EndAsync(DisconnectReason reason, string? message)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        EndReason = reason;
        StatusMessage = message;
        State = ControllerState.Ended;
        _cts.Cancel();
        _outbox.Writer.TryComplete();
        await _connection.DisposeAsync().ConfigureAwait(false);
        StateChanged?.Invoke();
        _completion.TrySetResult();
    }

    public async ValueTask DisposeAsync() => await EndAsync(DisconnectReason.ControllerDisconnected, null).ConfigureAwait(false);
}
