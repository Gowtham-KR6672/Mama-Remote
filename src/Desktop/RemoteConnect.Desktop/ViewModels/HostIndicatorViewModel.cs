using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnect.Protocol;
using RemoteConnect.Security;
using RemoteConnect.Session.Host;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.Desktop.ViewModels;

/// <summary>
/// The always-visible "someone is connected" panel on the host. The remote side cannot hide it; the person at this
/// computer can shrink it to a small bar that still shows who is connected and offers Disconnect.
/// </summary>
public sealed partial class HostIndicatorViewModel : ObservableObject
{
    private readonly HostSession _session;
    private readonly Action<bool>? _collapsedChanged;
    private bool _syncing;

    public HostIndicatorViewModel(HostSession session, bool startCollapsed = false, Action<bool>? collapsedChanged = null)
    {
        _session = session;
        _isCollapsed = startCollapsed;
        _collapsedChanged = collapsedChanged;
        _allowControl = session.Has(SessionPermissions.ControlInput);
        _allowClipboard = session.Has(SessionPermissions.Clipboard);
        _canEditControl = _allowControl;
        _canEditClipboard = _allowClipboard;
        session.PermissionsChanged += _ => Dispatcher.UIThread.Post(SyncFromSession);
        Tick();
    }

    public string ControllerName => _session.ControllerAlias;

    public string ControllerId => DeviceIds.Format(_session.ControllerId);

    public string Mode => _session.IsUnattended ? "Unattended access (password)" : "Approved by you";

    public string Route => _session.Path == ConnectionPath.Direct ? "Direct connection" : "Via relay server";

    [ObservableProperty]
    private string _elapsed = "00:00:00";

    [ObservableProperty]
    private bool _allowControl;

    [ObservableProperty]
    private bool _allowClipboard;

    // Permissions can be reduced and restored during the session, never raised above what was granted at start.
    [ObservableProperty]
    private bool _canEditControl;

    [ObservableProperty]
    private bool _canEditClipboard;

    [ObservableProperty]
    private bool _isCollapsed;

    partial void OnIsCollapsedChanged(bool value) => _collapsedChanged?.Invoke(value);

    [RelayCommand]
    private void ToggleCollapsed() => IsCollapsed = !IsCollapsed;

    public void Tick()
    {
        var t = DateTimeOffset.UtcNow - _session.StartedAt;
        Elapsed = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    partial void OnAllowControlChanged(bool value) => PushPermissions();

    partial void OnAllowClipboardChanged(bool value) => PushPermissions();

    private void PushPermissions()
    {
        if (_syncing)
        {
            return;
        }

        var p = SessionPermissions.ViewScreen
            | (AllowControl ? SessionPermissions.ControlInput : 0)
            | (AllowClipboard ? SessionPermissions.Clipboard : 0);
        _ = _session.SetPermissionsAsync(p);
    }

    private void SyncFromSession()
    {
        _syncing = true;
        AllowControl = _session.Has(SessionPermissions.ControlInput);
        AllowClipboard = _session.Has(SessionPermissions.Clipboard);
        _syncing = false;
    }

    [RelayCommand]
    private void Disconnect() => _session.End(DisconnectReason.Revoked, "Ended by the user at this computer.");
}
