using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnect.Desktop.Services;
using RemoteConnect.Protocol;
using RemoteConnect.Security;
using RemoteConnect.Session.Controller;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.Desktop.ViewModels;

public enum SessionPhase
{
    Connecting,
    Waiting,
    Connected,
    Ended,
}

/// <summary>One outgoing remote-control window.</summary>
public sealed partial class RemoteSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppHost _app;
    private readonly ConnectTarget _target;
    private readonly Func<UnknownHostInfo, Task<bool>> _confirmHost;
    private readonly DispatcherTimer _statsTimer;
    private CancellationTokenSource? _connectCts;
    private bool _layoutSync;
    private bool _recorded;

    public RemoteSessionViewModel(AppHost app, ConnectTarget target, Func<UnknownHostInfo, Task<bool>> confirmHost)
    {
        _app = app;
        _target = target;
        _confirmHost = confirmHost;
        _title = $"RemoteConnect — {target}";
        _quality = app.Settings.Quality;
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats());
    }

    public ObservableCollection<string> Monitors { get; } = new();

    public IReadOnlyList<QualityPreset> Qualities { get; } = Enum.GetValues<QualityPreset>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsBusy), nameof(IsEnded), nameof(ShowOverlay))]
    private SessionPhase _phase = SessionPhase.Connecting;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _statusText = "Connecting…";

    [ObservableProperty]
    private ControllerSession? _session;

    [ObservableProperty]
    private bool _showPassword;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string? _authMessage;

    [ObservableProperty]
    private string _statsText = string.Empty;

    [ObservableProperty]
    private string _routeText = string.Empty;

    [ObservableProperty]
    private string _permissionsText = string.Empty;

    [ObservableProperty]
    private bool _canControl;

    [ObservableProperty]
    private int _selectedMonitor;

    [ObservableProperty]
    private QualityPreset _quality;

    /// <summary>The device presented a different key than remembered; the user may explicitly accept the change.</summary>
    [ObservableProperty]
    private bool _identityChanged;

    public bool IsConnected => Phase == SessionPhase.Connected;

    public bool IsBusy => Phase is SessionPhase.Connecting or SessionPhase.Waiting;

    public bool IsEnded => Phase == SessionPhase.Ended;

    public bool ShowOverlay => Phase != SessionPhase.Connected;

    public event Action? CloseRequested;

    public async Task StartAsync()
    {
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;
        Phase = SessionPhase.Connecting;
        ShowPassword = false;
        AuthMessage = null;
        StatusText = $"Connecting to {_target}…";
        var progress = new Progress<string>(s => StatusText = s);

        try
        {
            var connection = await _app.Connector.ConnectAsync(_target, _confirmHost, progress, ct);
            var session = await ControllerSession.StartAsync(connection, _app.BuildHello(), _app.Clipboard, _app.Logger);
            Attach(session);
        }
        catch (OperationCanceledException)
        {
            End("Connection cancelled.");
        }
        catch (HostIdentityMismatchException ex)
        {
            IdentityChanged = true;
            End("⚠ Security warning: " + ex.Message);
        }
        catch (Exception ex)
        {
            End(ex is ConnectException ? ex.Message : $"Could not connect: {ex.Message}");
        }
    }

    private void Attach(ControllerSession session)
    {
        Session = session;
        RouteText = $"{(session.Path == ConnectionPath.Direct ? "Direct" : "Relay")} · {session.TlsProtocol.Replace("Tls", "TLS ")} · {CertificateFingerprint.ToDisplay(session.RemoteFingerprint, 4)}";
        session.StateChanged += () => Dispatcher.UIThread.Post(OnStateChanged);
        session.LayoutChanged += () => Dispatcher.UIThread.Post(OnLayoutChanged);
        session.PermissionsChanged += () => Dispatcher.UIThread.Post(UpdatePermissions);
        session.AuthStatusReceived += m => Dispatcher.UIThread.Post(() =>
        {
            AuthMessage = m.Success ? "Password accepted." : m.LockedOut ? m.Message : $"{m.Message} {m.AttemptsRemaining} attempt(s) left.";
        });
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        var s = Session;
        if (s is null)
        {
            return;
        }

        string host = string.IsNullOrEmpty(s.HostAlias) ? DeviceIds.Format(s.RemoteDeviceId) : $"{s.HostAlias} ({DeviceIds.Format(s.RemoteDeviceId)})";
        Title = $"RemoteConnect — {host}";
        switch (s.State)
        {
            case ControllerState.WaitingForApproval:
                Phase = SessionPhase.Waiting;
                ShowPassword = s.UnattendedAvailable;
                StatusText = s.PasswordOnly
                    ? $"{host} can only be reached with its unattended-access password right now."
                    : $"Waiting for the user at {host} to accept…";
                break;
            case ControllerState.Connected:
                Phase = SessionPhase.Connected;
                ShowPassword = false;
                UpdatePermissions();
                _statsTimer.Start();
                Record("Connected");
                if (Quality != QualityPreset.Balanced)
                {
                    s.SetQuality(Quality);
                }

                break;
            case ControllerState.Ended:
                _statsTimer.Stop();
                End(s.StatusMessage ?? "Session ended.");
                break;
        }
    }

    private void OnLayoutChanged()
    {
        var s = Session;
        if (s is null)
        {
            return;
        }

        _layoutSync = true;
        Monitors.Clear();
        foreach (var m in s.Monitors)
        {
            Monitors.Add($"{m.Name} · {m.Width}×{m.Height}");
        }

        SelectedMonitor = s.ActiveMonitor;
        _layoutSync = false;
    }

    private void UpdatePermissions()
    {
        var s = Session;
        if (s is null)
        {
            return;
        }

        CanControl = s.Has(SessionPermissions.ControlInput);
        PermissionsText = (CanControl ? "Control: on" : "View only") + (s.Has(SessionPermissions.Clipboard) ? " · Clipboard: on" : " · Clipboard: off");
    }

    private void UpdateStats()
    {
        var s = Session;
        if (s is null || s.State != ControllerState.Connected)
        {
            return;
        }

        var st = s.GetStats();
        StatsText = $"{st.Fps:0} fps · {st.Kbps / 1000:0.0} Mbit/s · {st.RttMs:0} ms";
    }

    partial void OnSelectedMonitorChanged(int value)
    {
        if (!_layoutSync && value >= 0 && Session is { State: ControllerState.Connected } s && value != s.ActiveMonitor)
        {
            s.SelectMonitor(value);
        }
    }

    partial void OnQualityChanged(QualityPreset value)
    {
        if (Session is { State: ControllerState.Connected } s)
        {
            s.SetQuality(value);
        }
    }

    [RelayCommand]
    private void SubmitPassword()
    {
        if (!string.IsNullOrEmpty(Password) && Session is { State: ControllerState.WaitingForApproval } s)
        {
            s.SendPassword(Password);
            Password = string.Empty;
            AuthMessage = "Checking password…";
        }
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        _connectCts?.Cancel();
        if (Session is { } s)
        {
            await s.DisconnectAsync();
        }
        else
        {
            End("Connection cancelled.");
        }
    }

    [RelayCommand]
    private async Task Reconnect()
    {
        if (Session is { } old)
        {
            await old.DisposeAsync();
            Session = null;
        }

        _recorded = false;
        await StartAsync();
    }

    [RelayCommand]
    private async Task TrustNewIdentity()
    {
        // Forget the pinned key for this target; the next connection pins whatever it presents
        // (by IP address the user is shown the new ID/fingerprint to confirm first).
        _app.Store.ForgetKey(_target.PinKey);
        IdentityChanged = false;
        await Reconnect();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private void End(string message)
    {
        Phase = SessionPhase.Ended;
        ShowPassword = false;
        StatusText = message;
        Record(message);
    }

    private void Record(string result)
    {
        if (_recorded)
        {
            return;
        }

        _recorded = true;
        var s = Session;
        _app.RecordRecent(new RecentSession(
            "Outgoing",
            _target.IsDeviceId ? _target.DeviceId! : _target.ToString(),
            s?.RemoteDeviceId ?? _target.DeviceId ?? string.Empty,
            s?.HostAlias ?? string.Empty,
            DateTimeOffset.Now,
            s is null ? string.Empty : s.Path.ToString(),
            result.Length > 120 ? result[..120] : result));
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer.Stop();
        _connectCts?.Cancel();
        if (Session is { } s)
        {
            if (s.State != ControllerState.Ended)
            {
                await s.DisconnectAsync();
            }

            await s.DisposeAsync();
        }
    }
}
