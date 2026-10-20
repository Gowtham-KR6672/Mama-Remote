using System.Reflection;
using Microsoft.Extensions.Logging;
using RemoteConnect.Platform;
using RemoteConnect.Protocol;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;
using RemoteConnect.Session.Controller;
using RemoteConnect.Session.Discovery;
using RemoteConnect.Session.Host;
using RemoteConnect.Session.Signaling;

namespace RemoteConnect.Desktop.Services;

/// <summary>
/// Composition root for the running app: identity, settings, server signaling, LAN discovery, the incoming-
/// connection host agent and the outgoing connector. Lives for the whole process.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly FileLoggerProvider _logProvider;
    private int _appliedLanPort = -1;

    private AppHost(SettingsStore store, DeviceIdentity identity, FileLoggerProvider logProvider, ILoggerFactory loggers, HostInteraction interaction, IClipboardBridge clipboard)
    {
        Store = store;
        Identity = identity;
        _logProvider = logProvider;
        Loggers = loggers;
        Interaction = interaction;
        Clipboard = clipboard;
        Logger = loggers.CreateLogger("RemoteConnect.App");

        Signaling = new SignalingClient(identity, loggers.CreateLogger("RemoteConnect.Signaling"))
        {
            LanEndpointsProvider = LanEndpoints,
        };
        Signaling.RelayOfferHandler = offer => Host!.AcceptRelayAsync(Signaling.RelayUri(offer.SessionId), offer);

        Discovery = new LanDiscovery(identity.DeviceId, loggers.CreateLogger("RemoteConnect.Discovery"));
        Host = new HostAgent(identity, BuildHostOptions, interaction, clipboard, loggers.CreateLogger("RemoteConnect.Host"));
        Connector = new Connector(identity, Signaling, Discovery, store, loggers.CreateLogger("RemoteConnect.Connector"));
    }

    public static string AppVersion { get; } =
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public SettingsStore Store { get; }

    public AppSettings Settings => Store.Settings;

    public DeviceIdentity Identity { get; }

    public ILoggerFactory Loggers { get; }

    public ILogger Logger { get; }

    public HostInteraction Interaction { get; }

    public IClipboardBridge Clipboard { get; }

    public SignalingClient Signaling { get; }

    public LanDiscovery Discovery { get; }

    public HostAgent Host { get; }

    public Connector Connector { get; }

    public event Action? RecentChanged;

    public static AppHost Create(HostInteraction interaction, IClipboardBridge clipboard)
    {
        var logProvider = new FileLoggerProvider(AppPaths.LogDirectory);
        var loggers = LoggerFactory.Create(b => b.AddProvider(logProvider).SetMinimumLevel(LogLevel.Information));
        var store = new SettingsStore();
        var identity = DeviceIdentityStore.LoadOrCreate(AppPaths.IdentityFile);
        var host = new AppHost(store, identity, logProvider, loggers, interaction, clipboard);
        host.Logger.LogInformation("RemoteConnect {Version} on {Platform}; device ID {Id}", AppVersion, PlatformServices.PlatformName, identity.DeviceId);
        return host;
    }

    public async Task StartAsync()
    {
        // Start at sign-in so the device is reachable without anyone opening the app (user-visible, can be disabled).
        if (!Settings.AutoStartApplied)
        {
            Settings.AutoStartApplied = true;
            ApplyAutoStart();
            Store.TrySave();
        }
        else if (Settings.AutoStart && PlatformServices.AutoStart.IsSupported && !PlatformServices.AutoStart.IsEnabled())
        {
            ApplyAutoStart(); // binary moved/updated: refresh the registered path
        }

        await ApplyNetworkSettingsAsync().ConfigureAwait(false);
    }

    /// <summary>Re-applies listener, discovery and server settings after the user saved Settings.</summary>
    public async Task ApplyNetworkSettingsAsync()
    {
        if (Settings.LanListenerEnabled && Settings.AllowIncoming)
        {
            if (Host.ListeningPort is null || _appliedLanPort != Settings.LanPort)
            {
                Host.StartLan(Settings.LanPort);
                _appliedLanPort = Settings.LanPort;
            }
        }
        else
        {
            Host.StopLan();
            _appliedLanPort = -1;
        }

        Connector.DefaultPort = Settings.LanPort;
        if (Settings.LanDiscoveryEnabled)
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

        await Signaling.ConfigureAsync(new SignalingOptions(
            Settings.ServerUrl, Store.RegistrationKey, Settings.Alias, PlatformServices.PlatformName, AppVersion)).ConfigureAwait(false);
    }

    public void ApplyAutoStart()
    {
        try
        {
            if (PlatformServices.AutoStart.IsSupported)
            {
                PlatformServices.AutoStart.SetEnabled(Settings.AutoStart);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not update start-at-login: {Message}", ex.Message);
        }
    }

    public HostOptions BuildHostOptions()
    {
        var s = Settings;
        var attended = SessionPermissions.ViewScreen
            | (s.AllowControl ? SessionPermissions.ControlInput : 0)
            | (s.AllowClipboard ? SessionPermissions.Clipboard : 0);
        var unattended = SessionPermissions.ViewScreen
            | (s.UnattendedAllowControl ? SessionPermissions.ControlInput : 0)
            | (s.AllowClipboard ? SessionPermissions.Clipboard : 0);
        return new HostOptions
        {
            Alias = s.Alias,
            Platform = PlatformServices.PlatformName,
            AppVersion = AppVersion,
            AllowIncoming = s.AllowIncoming,
            AttendedPermissions = attended,
            UnattendedEnabled = s.UnattendedEnabled,
            UnattendedPasswordHash = s.UnattendedPasswordHash,
            UnattendedAllowList = s.UnattendedAllowList.ToArray(),
            UnattendedPermissions = unattended,
            DefaultQuality = QualityPreset.Balanced,
        };
    }

    public HelloMessage BuildHello() => new(
        ProtocolInfo.Version,
        Identity.DeviceId,
        Settings.Alias,
        PlatformServices.PlatformName,
        AppVersion,
        SessionPermissions.ViewScreen | SessionPermissions.ControlInput | SessionPermissions.Clipboard);

    private LanAnnouncement? BuildAnnouncement()
    {
        int? port = Host.ListeningPort;
        if (!Settings.AllowIncoming || port is null)
        {
            return null;
        }

        return new LanAnnouncement(1, Identity.DeviceId, Settings.Alias, PlatformServices.PlatformName, port.Value, Identity.Fingerprint);
    }

    public string[] LanEndpoints()
        => Settings.AllowIncoming && Host.ListeningPort is int port ? NetworkInfo.Endpoints(port) : Array.Empty<string>();

    public void RecordRecent(RecentSession session)
    {
        Store.AddRecent(session);
        RecentChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Logger.LogInformation("Shutting down");
        await Host.DisposeAsync().ConfigureAwait(false);
        Discovery.Dispose();
        await Signaling.DisposeAsync().ConfigureAwait(false);
        Loggers.Dispose();
        _logProvider.Dispose();
    }
}
