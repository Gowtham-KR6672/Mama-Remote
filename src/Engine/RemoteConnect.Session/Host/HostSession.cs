using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RemoteConnect.Platform;
using RemoteConnect.Protocol;
using RemoteConnect.Session.Media;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.Session.Host;

/// <summary>
/// A running, approved session on the host: streams the screen and applies input — but only what
/// <see cref="Granted"/> allows, checked on every message. The local user can change permissions or end it anytime.
/// </summary>
public sealed class HostSession
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisplayRefreshInterval = TimeSpan.FromSeconds(3);

    private readonly PeerConnection _connection;
    private readonly Func<IScreenCapturer> _capturerFactory;
    private readonly Func<IInputInjector> _injectorFactory;
    private readonly IClipboardBridge? _clipboard;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _frameCredits = new(2, 2);
    private readonly object _sync = new();

    private IReadOnlyList<DisplayInfo> _displays = Array.Empty<DisplayInfo>();
    private int _activeDisplay;
    private volatile bool _forceKeyFrame = true;
    private volatile int _quality;
    private IInputInjector? _injector;
    private string? _lastClipboard;
    private long _lastActivityTicks = Stopwatch.GetTimestamp();
    private DisconnectReason _endReason = DisconnectReason.ControllerDisconnected;
    private string? _endDetail;
    private int _framesSent;

    internal HostSession(
        PeerConnection connection,
        HelloMessage hello,
        SessionPermissions granted,
        bool unattended,
        QualityPreset quality,
        Func<IScreenCapturer> capturerFactory,
        Func<IInputInjector> injectorFactory,
        IClipboardBridge? clipboard,
        ILogger logger)
    {
        _connection = connection;
        _capturerFactory = capturerFactory;
        _injectorFactory = injectorFactory;
        _clipboard = clipboard;
        _logger = logger;
        Granted = granted | SessionPermissions.ViewScreen;
        IsUnattended = unattended;
        _quality = (int)quality;
        ControllerId = connection.RemoteDeviceId;
        ControllerAlias = string.IsNullOrWhiteSpace(hello.Alias) ? "Unknown" : hello.Alias.Trim()[..Math.Min(64, hello.Alias.Trim().Length)];
        ControllerPlatform = hello.Platform;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public string ControllerId { get; }

    public string ControllerAlias { get; }

    public string ControllerPlatform { get; }

    public string ControllerFingerprint => _connection.RemoteFingerprint;

    public string RemoteEndpoint => _connection.RemoteEndpoint;

    public ConnectionPath Path => _connection.Path;

    public bool IsUnattended { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public SessionPermissions Granted { get; private set; }

    public int FramesSent => _framesSent;

    public long BytesSent => _connection.BytesSent;

    public event Action<HostSession>? PermissionsChanged;

    public bool Has(SessionPermissions permission) => (Granted & permission) == permission;

    /// <summary>Host-side permission change (e.g. the user turns remote control off). Takes effect immediately.</summary>
    public async Task SetPermissionsAsync(SessionPermissions granted)
    {
        granted |= SessionPermissions.ViewScreen;
        lock (_sync)
        {
            if (granted == Granted)
            {
                return;
            }

            Granted = granted;
        }

        if ((granted & SessionPermissions.ControlInput) == 0)
        {
            _injector?.ReleaseAll();
        }

        PermissionsChanged?.Invoke(this);
        try
        {
            await _connection.SendJsonAsync(MessageType.PermissionsChanged, new PermissionsChangedMessage(granted), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Ends the session from the host side (Disconnect / Revoke button, app exit).</summary>
    public void End(DisconnectReason reason = DisconnectReason.Revoked, string? detail = null)
    {
        lock (_sync)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _endReason = reason;
            _endDetail = detail;
        }

        _cts.Cancel();
    }

    internal async Task<(DisconnectReason Reason, string? Detail)> RunAsync()
    {
        var ct = _cts.Token;
        using var capturer = _capturerFactory();
        using var injector = _injectorFactory();
        _injector = injector;
        try
        {
            _displays = capturer.GetDisplays();
            if (_displays.Count == 0)
            {
                End(DisconnectReason.HostDisconnected, "No display available to share.");
            }
            else
            {
                await SendLayoutAsync(ct).ConfigureAwait(false);
                if (_clipboard is not null)
                {
                    // Only clipboard changes made during the session are shared, never what was copied before.
                    _lastClipboard = await SafeGetClipboardAsync().ConfigureAwait(false);
                }

                var tasks = new[]
                {
                    Task.Run(() => CaptureLoopAsync(capturer, ct), ct),
                    Task.Run(() => ReceiveLoopAsync(injector, ct), ct),
                    Task.Run(() => HousekeepingLoopAsync(capturer, ct), ct),
                };
                await Task.WhenAny(tasks).ConfigureAwait(false);
                _cts.Cancel();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Individual loop failures are reflected in the end reason.
                }
            }
        }
        finally
        {
            injector.ReleaseAll();
            _injector = null;
            await _connection.TrySendAsync(
                MessageType.Disconnect,
                MessageFramer.SerializeJson(new DisconnectMessage(_endReason, _endDetail)),
                TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        return (_endReason, _endDetail);
    }

    private async Task CaptureLoopAsync(IScreenCapturer capturer, CancellationToken ct)
    {
        var encoder = new TileEncoder();
        var current = new FrameBuffer();
        var previous = new FrameBuffer();
        bool havePrevious = false;
        var clock = Stopwatch.StartNew();
        int consecutiveFailures = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var q = QualitySettings.For((QualityPreset)_quality);
                var started = clock.Elapsed;
                await _frameCredits.WaitAsync(ct).ConfigureAwait(false);
                bool sent = false;
                try
                {
                    if (_forceKeyFrame)
                    {
                        _forceKeyFrame = false;
                        havePrevious = false;
                    }

                    DisplayInfo display;
                    lock (_sync)
                    {
                        display = _displays[Math.Clamp(_activeDisplay, 0, _displays.Count - 1)];
                    }

                    if (capturer.Capture(display, current, q.MaxWidth, q.MaxHeight))
                    {
                        consecutiveFailures = 0;
                        encoder.Quality = q.JpegQuality;
                        var update = encoder.Encode(current, havePrevious ? previous : null, forceKeyFrame: !havePrevious);
                        if (update is not null)
                        {
                            await _connection.SendAsync(MessageType.FrameUpdate, update.Encode(), ct).ConfigureAwait(false);
                            sent = true;
                            Interlocked.Increment(ref _framesSent);
                            (current, previous) = (previous, current);
                            havePrevious = true;
                        }
                    }
                    else if (++consecutiveFailures % 50 == 1)
                    {
                        _logger.LogInformation("Screen capture unavailable (secure desktop, locked session or missing permission).");
                    }
                }
                finally
                {
                    if (!sent)
                    {
                        _frameCredits.Release();
                    }
                }

                var remaining = q.FrameInterval - (clock.Elapsed - started);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture loop failed");
            End(DisconnectReason.HostDisconnected, "Screen capture failed: " + ex.Message);
        }
    }

    private async Task ReceiveLoopAsync(IInputInjector injector, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var frame = await _connection.ReceiveAsync(ct).ConfigureAwait(false);
                if (frame is null)
                {
                    End(DisconnectReason.ControllerDisconnected, _connection.Error?.Message);
                    return;
                }

                Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());
                await HandleAsync(frame.Value, injector, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ProtocolException ex)
        {
            _logger.LogWarning("Protocol error from {Controller}: {Message}", ControllerId, ex.Message);
            End(DisconnectReason.ProtocolError, ex.Message);
        }
    }

    internal async Task HandleAsync(Frame frame, IInputInjector injector, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case MessageType.FrameAck:
                SimpleMessages.DecodeUInt32(frame.Span);
                try
                {
                    _frameCredits.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Unsolicited ack; ignore.
                }

                break;

            case MessageType.Pointer:
            {
                var m = PointerMessage.Decode(frame.Span);
                if (!Has(SessionPermissions.ControlInput))
                {
                    break;
                }

                var d = ActiveDisplay();
                double nx = m.X / 65535.0, ny = m.Y / 65535.0;
                if (m.Action == PointerAction.Move)
                {
                    injector.MovePointer(d, nx, ny);
                }
                else
                {
                    injector.PointerButton(d, nx, ny, m.Button, m.Action == PointerAction.Down);
                }

                break;
            }

            case MessageType.Scroll:
            {
                var m = ScrollMessage.Decode(frame.Span);
                if (Has(SessionPermissions.ControlInput))
                {
                    injector.Scroll(ActiveDisplay(), m.X / 65535.0, m.Y / 65535.0, m.DeltaX, m.DeltaY);
                }

                break;
            }

            case MessageType.Key:
            {
                var m = KeyMessage.Decode(frame.Span);
                if (Has(SessionPermissions.ControlInput))
                {
                    injector.Key(m.HidUsage, m.Down);
                }

                break;
            }

            case MessageType.SelectMonitor:
            {
                var m = MessageFramer.ParseJson<SelectMonitorMessage>(frame);
                lock (_sync)
                {
                    if (m.Index < 0 || m.Index >= _displays.Count)
                    {
                        break;
                    }

                    _activeDisplay = m.Index;
                }

                injector.ReleaseAll();
                _forceKeyFrame = true;
                await SendLayoutAsync(ct).ConfigureAwait(false);
                break;
            }

            case MessageType.SetQuality:
            {
                var m = MessageFramer.ParseJson<SetQualityMessage>(frame);
                if (Enum.IsDefined(m.Preset))
                {
                    _quality = (int)m.Preset;
                    _forceKeyFrame = true;
                }

                break;
            }

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
                await _connection.SendAsync(MessageType.Pong, frame.Memory, ct).ConfigureAwait(false);
                break;

            case MessageType.Pong:
                break;

            case MessageType.Disconnect:
                End(DisconnectReason.ControllerDisconnected, "The remote side ended the session.");
                break;

            default:
                // Unknown or out-of-phase messages are ignored rather than trusted.
                break;
        }
    }

    private async Task HousekeepingLoopAsync(IScreenCapturer capturer, CancellationToken ct)
    {
        var lastDisplayCheck = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

                // A dead transport must not keep a session (or injected input) alive.
                var idle = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivityTicks));
                if (idle > IdleTimeout)
                {
                    End(DisconnectReason.Timeout, "The remote side stopped responding.");
                    return;
                }

                if (idle > PingInterval)
                {
                    await _connection.SendAsync(MessageType.Ping, SimpleMessages.EncodeInt64(Stopwatch.GetTimestamp()), ct).ConfigureAwait(false);
                }

                if (lastDisplayCheck.Elapsed > DisplayRefreshInterval)
                {
                    lastDisplayCheck.Restart();
                    await RefreshDisplaysAsync(capturer, ct).ConfigureAwait(false);
                }

                if (_clipboard is not null && Has(SessionPermissions.Clipboard))
                {
                    var text = await SafeGetClipboardAsync().ConfigureAwait(false);
                    if (text is not null && text != _lastClipboard && text.Length <= ClipboardTextMessage.MaxChars)
                    {
                        _lastClipboard = text;
                        await _connection.SendJsonAsync(MessageType.ClipboardText, new ClipboardTextMessage(text), ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            End(DisconnectReason.ControllerDisconnected, ex.Message);
        }
    }

    private async Task RefreshDisplaysAsync(IScreenCapturer capturer, CancellationToken ct)
    {
        var fresh = capturer.GetDisplays();
        if (fresh.Count == 0)
        {
            return;
        }

        bool changed;
        lock (_sync)
        {
            changed = fresh.Count != _displays.Count || fresh.Where((d, i) => d != _displays[i]).Any();
            if (changed)
            {
                _displays = fresh;
                _activeDisplay = Math.Clamp(_activeDisplay, 0, fresh.Count - 1);
            }
        }

        if (changed)
        {
            _injector?.ReleaseAll();
            _forceKeyFrame = true;
            await SendLayoutAsync(ct).ConfigureAwait(false);
        }
    }

    private DisplayInfo ActiveDisplay()
    {
        lock (_sync)
        {
            return _displays[Math.Clamp(_activeDisplay, 0, _displays.Count - 1)];
        }
    }

    private Task SendLayoutAsync(CancellationToken ct)
    {
        ScreenLayoutMessage layout;
        lock (_sync)
        {
            layout = new ScreenLayoutMessage(
                _displays.Select(d => new RemoteMonitor(d.Index, d.Name, d.IsPrimary, d.PixelWidth, d.PixelHeight)).ToList(),
                _activeDisplay);
        }

        return _connection.SendJsonAsync(MessageType.ScreenLayout, layout, ct);
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
}
