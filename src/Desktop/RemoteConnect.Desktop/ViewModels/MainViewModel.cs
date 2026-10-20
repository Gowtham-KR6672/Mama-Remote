using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnect.Desktop.Services;
using RemoteConnect.Platform;
using RemoteConnect.Security;
using RemoteConnect.Session.Controller;
using RemoteConnect.Session.Discovery;
using RemoteConnect.Session.Signaling;

namespace RemoteConnect.Desktop.ViewModels;

public sealed record RecentItem(string Title, string Subtitle, string Target, string When);

public sealed record PeerItem(string Title, string Subtitle, string Target);

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#EF4444"));

    private readonly AppHost _app;
    private readonly Action<ConnectTarget> _openSession;
    private readonly Func<string, Task> _copyText;

    public MainViewModel(AppHost app, Action<ConnectTarget> openSession, Func<string, Task> copyText)
    {
        _app = app;
        _openSession = openSession;
        _copyText = copyText;
        Settings = new SettingsViewModel(app);

        app.Signaling.StatusChanged += () => Dispatcher.UIThread.Post(RefreshStatus);
        app.Discovery.PeersChanged += () => Dispatcher.UIThread.Post(RefreshPeers);
        app.RecentChanged += () => Dispatcher.UIThread.Post(RefreshRecent);
        app.Host.SessionsChanged += () => Dispatcher.UIThread.Post(RefreshStatus);
        app.Store.Saved += () => Dispatcher.UIThread.Post(RefreshStatus);

        RefreshStatus();
        RefreshRecent();
        RefreshPeers();
    }

    public SettingsViewModel Settings { get; }


    public ObservableCollection<RecentItem> Recent { get; } = new();

    public ObservableCollection<PeerItem> Peers { get; } = new();

    [ObservableProperty] private string _deviceIdDisplay = string.Empty;
    [ObservableProperty] private string _alias = string.Empty;
    [ObservableProperty] private string _remoteAddress = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private IBrush _statusBrush = Amber;
    [ObservableProperty] private string _lanText = string.Empty;
    [ObservableProperty] private string _incomingText = string.Empty;
    [ObservableProperty] private string? _connectError;
    [ObservableProperty] private string? _permissionHint;
    [ObservableProperty] private bool _hasNoRecent;
    [ObservableProperty] private bool _hasNoPeers;
    [ObservableProperty] private string? _copyFeedback;

    [RelayCommand]
    private void Connect()
    {
        ConnectError = null;
        if (!ConnectTargetParser.TryParse(RemoteAddress, _app.Settings.LanPort, out var target, out var error))
        {
            ConnectError = error;
            return;
        }

        _openSession(target);
    }

    [RelayCommand]
    private void ConnectTo(string target)
    {
        RemoteAddress = target;
        Connect();
    }

    [RelayCommand]
    private async Task CopyId()
    {
        await _copyText(_app.IncomingDeviceId);
        CopyFeedback = "Copied";
        await Task.Delay(1500);
        CopyFeedback = null;
    }

    [RelayCommand]
    private void GrantPermissions()
    {
        PlatformServices.RequestPermissions();
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var s = _app.Settings;
        Alias = s.Alias;
        DeviceIdDisplay = DeviceIds.Format(_app.IncomingDeviceId);
        if (_app.ServiceModeInstalled)
        {
            RefreshServiceModeStatus();
            return;
        }

        var sig = _app.Signaling;
        bool lan = _app.Host.ListeningPort is not null;
        (StatusBrush, StatusText) = sig.Status switch
        {
            SignalingStatus.Online => (Green, "Ready — online via server"),
            SignalingStatus.Disabled when lan => (Green, "Ready on the local network"),
            SignalingStatus.Connecting => (Amber, sig.StatusMessage ?? "Connecting…"),
            SignalingStatus.Rejected => (Red, sig.StatusMessage ?? "Server refused this device"),
            _ when lan => (Amber, (sig.StatusMessage ?? "Server offline") + " — LAN still works"),
            _ => (Red, sig.StatusMessage ?? "Offline"),
        };

        if (!s.AllowIncoming)
        {
            StatusBrush = Amber;
            StatusText = "Incoming connections are turned off";
        }

        LanText = _app.Host.ListeningPort is int port
            ? "LAN: " + string.Join(", ", NetworkInfo.Endpoints(port).DefaultIfEmpty($"port {port}"))
            : _app.Host.ListenerError ?? "Direct LAN connections are off";

        IncomingText = s.UnattendedEnabled && !string.IsNullOrEmpty(s.UnattendedPasswordHash)
            ? "Unattended access: ON (password)"
            : "Unattended access: off — someone must accept each request";

        var active = _app.Host.Sessions;
        if (active.Count > 0)
        {
            IncomingText = $"Being controlled by {active[0].ControllerAlias} ({DeviceIds.Format(active[0].ControllerId)})";
        }

        PermissionHint = PlatformServices.CheckPermissions().Hint;
    }

    /// <summary>Service mode: incoming connections belong to the service; show its state instead of this app's.</summary>
    private void RefreshServiceModeStatus()
    {
        var service = _app.ServiceStatus;
        (StatusBrush, StatusText) = service?.State switch
        {
            "Ready" => (Green, "Ready — reachable through the RemoteConnect service, even at the sign-in screen"),
            "Degraded" => (Amber, "RemoteConnect service running with problems (see its log)"),
            "Blocked" => (Red, "The RemoteConnect service cannot start its agent (see the Windows Event Log)"),
            "Stopped" => (Red, "The RemoteConnect service is stopped"),
            null => (Amber, "Waiting for the RemoteConnect service…"),
            _ => (Amber, "RemoteConnect service: " + service.State),
        };
        LanText = "Incoming connections are handled by the RemoteConnect service";
        IncomingText = service?.UnattendedReady == true
            ? "Unattended access: ON (service password)"
            : "Unattended access: OFF — nobody can connect until an administrator runs RemoteConnect.HostService.exe set-password";
        PermissionHint = null;
    }

    private void RefreshRecent()
    {
        Recent.Clear();
        foreach (var r in _app.Settings.Recent.Take(30))
        {
            string who = !string.IsNullOrEmpty(r.Alias) ? r.Alias : r.DeviceId.Length == 9 ? DeviceIds.Format(r.DeviceId) : r.Target;
            string target = DeviceIds.TryNormalize(r.Target, out var id) ? DeviceIds.Format(id) : r.Target;
            string arrow = r.Direction == "Incoming" ? "← incoming" : "→ outgoing";
            Recent.Add(new RecentItem(who, $"{target} · {arrow} · {r.Result}", r.Direction == "Incoming" && r.DeviceId.Length == 9 ? r.DeviceId : r.Target, r.When.LocalDateTime.ToString("g")));
        }

        HasNoRecent = Recent.Count == 0;
    }

    private void RefreshPeers()
    {
        Peers.Clear();
        foreach (var p in _app.Discovery.Peers)
        {
            Peers.Add(new PeerItem(p.Alias, $"{DeviceIds.Format(p.DeviceId)} · {p.Platform} · {p.Address}", p.DeviceId));
        }

        HasNoPeers = Peers.Count == 0;
    }
}
