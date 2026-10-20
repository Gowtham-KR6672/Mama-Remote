using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RemoteConnect.HostService;

/// <summary>Wakes the supervisor when Windows reports a session change (sign-in, sign-out, user switch, RDP).</summary>
public sealed class ConsoleSessionSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Raise() => _channel.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken ct) => _channel.Reader.WaitToReadAsync(ct);

    public void Reset() => _channel.Reader.TryRead(out _);
}

/// <summary>
/// The standard Windows-service lifetime, plus SERVICE_CONTROL_SESSIONCHANGE notifications. The supervisor also
/// polls <c>WTSGetActiveConsoleSessionId</c>, so a missed notification only delays a switch by a second.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SessionAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly ConsoleSessionSignal _signal;
    private readonly ILogger _logger;

    public SessionAwareServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        ConsoleSessionSignal signal)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _signal = signal;
        _logger = loggerFactory.CreateLogger("RemoteConnect.HostService.Sessions");
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        _logger.LogInformation("Session change: {Reason} (session {Session})", changeDescription.Reason, changeDescription.SessionId);
        _signal.Raise();
        base.OnSessionChange(changeDescription);
    }
}
