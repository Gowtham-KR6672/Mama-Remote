using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using RemoteConnect.Desktop.Services;
using RemoteConnect.LocalIpc;
using RemoteConnect.Platform;
using RemoteConnect.Platform.Windows;
using RemoteConnect.Protocol;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;
using RemoteConnect.Session.Discovery;
using RemoteConnect.Session.Host;
using RemoteConnect.Session.Signaling;

namespace RemoteConnect.Desktop.Agent;

/// <summary>
/// Composition root of the service-mode session agent: a headless host (no main window, no tray) with the machine's
/// own identity and settings. Capture and input follow the input desktop, so the sign-in and lock screens work.
/// Nobody can answer a prompt there, so only the opt-in unattended password grants access; the always-visible
/// session panel still appears on the user's desktop once someone is signed in.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentHost : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServiceSilenceLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettingsPollInterval = TimeSpan.FromSeconds(5);

    private readonly IpcChannel _service;
    private readonly FileLoggerProvider _logProvider;
    private readonly ILoggerFactory _loggers;
    private readonly CancellationTokenSource _cts = new();
    private volatile ServiceModeSettings _settings;
    private string? _settingsError;
    private (DateTime Settings, DateTime Key) _stamps;
    private (string? Server, string? Key, string Alias)? _appliedSignaling;
    private int _appliedLanPort = -1;
    private long _lastServiceHeartbeat = Environment.TickCount64;
    private AgentStatusMessage? _lastSent;

    private AgentHost(IpcChannel service, DeviceIdentity identity, FileLoggerProvider logProvider, ILoggerFactory loggers, IHostInteraction interaction)
    {
        _service = service;
        Identity = identity;
        _logProvider = logProvider;
        _loggers = loggers;
        Logger = loggers.CreateLogger("RemoteConnect.Agent");
        _settings = LoadSettings();

        Signaling = new SignalingClient(identity, loggers.CreateLogger("RemoteConnect.Signaling"))
        {
            LanEndpointsProvider = () => Host!.ListeningPort is int port ? NetworkInfo.Endpoints(port) : Array.Empty<string>(),
        };
        Signaling.RelayOfferHandler = offer => Host!.AcceptRelayAsync(Signaling.RelayUri(offer.SessionId), offer);
        Discovery = new LanDiscovery(identity.DeviceId, loggers.CreateLogger("RemoteConnect.Discovery"));
        Host = new HostAgent(identity, BuildHostOptions, interaction, clipboard: null, loggers.CreateLogger("RemoteConnect.Host"),
            PlatformServices.CreateInputDesktopCapturer, PlatformServices.CreateInputDesktopInjector);
    }

    public DeviceIdentity Identity { get; }

    public ILogger Logger { get; }

    public SignalingClient Signaling { get; }

    public LanDiscovery Discovery { get; }

    public HostAgent Host { get; }

    public static AgentHost Create(IpcChannel service, IHostInteraction interaction)
    {
        var logProvider = new FileLoggerProvider(AppPaths.LogDirectory);
        var loggers = LoggerFactory.Create(b => b.AddProvider(logProvider).SetMinimumLevel(LogLevel.Information));
        // Generated on first run as LocalSystem; DPAPI (LocalSystem account) inside the LocalSystem profile.
        var identity = DeviceIdentityStore.LoadOrCreate(AppPaths.IdentityFile);
        var host = new AgentHost(service, identity, logProvider, loggers, interaction);
        host.Logger.LogInformation("RemoteConnect {Version} session agent (service mode); device ID {Id}; input desktop {Desktop}",
            AppHost.AppVersion, identity.DeviceId, InputDesktop.TryGetName() ?? "(not accessible)");
        return host;
    }

    /// <summary>Runs until the service closes the pipe or goes silent; then the agent ends its sessions and exits.</summary>
    public async Task RunAsync()
    {
        var ct = _cts.Token;
        await SendStatusAsync(AgentState.Starting, ct).ConfigureAwait(false);
        await ApplyNetworkSettingsAsync().ConfigureAwait(false);
        await SendStatusAsync(null, ct).ConfigureAwait(false);

        var receive = ReceiveLoopAsync(ct);
        var housekeeping = HousekeepingLoopAsync(ct);
        var finished = await Task.WhenAny(receive, housekeeping).ConfigureAwait(false);
        _cts.Cancel();
        try
        {
            await Task.WhenAll(receive, housekeeping).ConfigureAwait(false);
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }

        if (finished.Exception?.GetBaseException() is { } error)
        {
            Logger.LogError(error, "Service link failed; stopping the agent");
        }
        else
        {
            Logger.LogInformation("Service link ended ({Reason}); stopping the agent", finished == receive ? "pipe closed" : "service silent");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _service.ReceiveAsync(ct).ConfigureAwait(false) is { } message)
            {
                if (message is HeartbeatMessage)
                {
                    Interlocked.Exchange(ref _lastServiceHeartbeat, Environment.TickCount64);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or IpcProtocolException or ObjectDisposedException)
        {
            Logger.LogWarning("Service pipe error: {Message}", ex.Message);
        }
    }

    private async Task HousekeepingLoopAsync(CancellationToken ct)
    {
        long sequence = 0;
        var settingsClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastServiceHeartbeat) > ServiceSilenceLimit.TotalMilliseconds)
                {
                    return;
                }

                await _service.SendAsync(new HeartbeatMessage(sequence++), ct).ConfigureAwait(false);
                if (settingsClock.Elapsed >= SettingsPollInterval)
                {
                    settingsClock.Restart();
                    if (ReloadSettingsIfChanged())
                    {
                        await ApplyNetworkSettingsAsync().ConfigureAwait(false);
                    }
                }

                await SendStatusAsync(null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task ApplyNetworkSettingsAsync()
    {
        var s = _settings;
        if (s.LanListenerEnabled)
        {
            if (Host.ListeningPort is null || _appliedLanPort != s.LanPort)
            {
                Host.StartLan(s.LanPort);
                _appliedLanPort = s.LanPort;
            }
        }
        else
        {
            Host.StopLan();
            _appliedLanPort = -1;
        }

        if (s.LanDiscoveryEnabled)
        {
            if (!Discovery.IsRunning)
            {
                Discovery.Start(BuildAnnouncement);
            }
        }
        else
        {
            Discovery.Stop();
        }

        var key = ReadRegistrationKey();
        var signaling = (s.ServerUrl, key, s.Alias);
        if (_appliedSignaling != signaling)
        {
            _appliedSignaling = signaling;
            await Signaling.ConfigureAsync(new SignalingOptions(s.ServerUrl, key, s.Alias, PlatformServices.PlatformName, AppHost.AppVersion)).ConfigureAwait(false);
        }
    }

    private HostOptions BuildHostOptions()
    {
        var s = _settings;
        return new HostOptions
        {
            Alias = s.Alias,
            Platform = PlatformServices.PlatformName,
            AppVersion = AppHost.AppVersion,
            AllowIncoming = true,
            AttendedApprovalAvailable = false,
            AttendedPermissions = SessionPermissions.ViewScreen,
            UnattendedEnabled = s.UnattendedEnabled,
            UnattendedPasswordHash = s.UnattendedPasswordHash,
            UnattendedAllowList = s.UnattendedAllowList.ToArray(),
            // No clipboard in service mode: the agent runs as LocalSystem and may be serving the sign-in screen.
            UnattendedPermissions = SessionPermissions.ViewScreen | (s.UnattendedAllowControl ? SessionPermissions.ControlInput : 0),
            DefaultQuality = QualityPreset.Balanced,
        };
    }

    private LanAnnouncement? BuildAnnouncement()
        => Host.ListeningPort is int port
            ? new LanAnnouncement(1, Identity.DeviceId, _settings.Alias, PlatformServices.PlatformName, port, Identity.Fingerprint)
            : null;

    private async Task SendStatusAsync(AgentState? state, CancellationToken ct)
    {
        var s = _settings;
        bool degraded = (s.LanListenerEnabled && Host.ListenerError is not null) || Signaling.Status == SignalingStatus.Rejected || _settingsError is not null;
        var status = new AgentStatusMessage(
            state ?? (degraded ? AgentState.Degraded : AgentState.Ready),
            Identity.DeviceId,
            Identity.Fingerprint,
            SafeName(InputDesktop.TryGetName()),
            Host.ListeningPort,
            Signaling.Status switch
            {
                SignalingStatus.Connecting => AgentServerStatus.Connecting,
                SignalingStatus.Online => AgentServerStatus.Online,
                SignalingStatus.Offline => AgentServerStatus.Offline,
                SignalingStatus.Rejected => AgentServerStatus.Rejected,
                _ => AgentServerStatus.Disabled,
            },
            Math.Min(Host.Sessions.Count, IpcCodec.MaxActiveSessions),
            s.UnattendedReady,
            SafeName(AppHost.AppVersion));
        if (status == _lastSent)
        {
            return;
        }

        await _service.SendAsync(status, ct).ConfigureAwait(false);
        _lastSent = status;
    }

    private static string SafeName(string? value)
    {
        var clean = new string((value ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '+').ToArray());
        return clean.Length > IpcCodec.MaxTextLength ? clean[..IpcCodec.MaxTextLength] : clean.Length == 0 ? "unknown" : clean;
    }

    private ServiceModeSettings LoadSettings()
    {
        _stamps = (Stamp(ServiceModeLayout.SettingsFile), Stamp(ServiceModeLayout.RegistrationKeyFile));
        var settings = ServiceModeSettingsFile.Load(ServiceModeLayout.SettingsFile, out _settingsError);
        if (_settingsError is not null)
        {
            Logger.LogError("Service settings unreadable ({Error}); using defaults with unattended access OFF", _settingsError);
        }

        return settings;
    }

    private bool ReloadSettingsIfChanged()
    {
        if ((Stamp(ServiceModeLayout.SettingsFile), Stamp(ServiceModeLayout.RegistrationKeyFile)) == _stamps)
        {
            return false;
        }

        _settings = LoadSettings();
        Logger.LogInformation("Service settings reloaded; unattended access {State}", _settings.UnattendedReady ? "ON" : "OFF");
        return true;
    }

    private static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

    private static string? ReadRegistrationKey()
    {
        var bytes = ProtectedFileStore.TryRead(ServiceModeLayout.RegistrationKeyFile, machineScope: true);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        Logger.LogInformation("Agent shutting down");
        _cts.Cancel();
        await Host.DisposeAsync().ConfigureAwait(false); // ends sessions: input released, controllers told why
        Discovery.Dispose();
        await Signaling.DisposeAsync().ConfigureAwait(false);
        await _service.DisposeAsync().ConfigureAwait(false);
        _loggers.Dispose();
        _logProvider.Dispose();
    }
}
