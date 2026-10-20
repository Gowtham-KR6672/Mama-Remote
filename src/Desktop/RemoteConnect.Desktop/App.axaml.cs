using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using RemoteConnect.Desktop.Agent;
using RemoteConnect.Desktop.Services;
using RemoteConnect.Desktop.ViewModels;
using RemoteConnect.Desktop.Views;
using RemoteConnect.Protocol;
using RemoteConnect.Security;
using RemoteConnect.Session.Controller;

namespace RemoteConnect.Desktop;

public partial class App : Application
{
    private AppHost? _host;
    private MainWindow? _main;
    private TrayIcon? _tray;
    private bool _quitting;

    /// <summary>Started at sign-in: stay in the tray instead of opening the window.</summary>
    public static bool StartHidden { get; set; }

    public static SingleInstance? Instance { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (OperatingSystem.IsWindows() && AgentMode.IsActive && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime agent)
        {
            // Service-mode session agent: no main window, no tray; only the session panel when someone connects.
            agent.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AgentMode.Start(agent);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var interaction = new HostInteraction();
            MainWindow? mainRef = null;
            var clipboard = new AvaloniaClipboardBridge(() => mainRef);
            _host = AppHost.Create(interaction, clipboard);
            interaction.Attach(_host);
            SettingsViewModel.ApplyTheme(_host.Settings.Theme);

            var mainVm = new MainViewModel(_host, OpenSession, CopyTextAsync);
            mainVm.Settings.UninstallHandler = () => UninstallAsync(desktop);
            _main = new MainWindow { DataContext = mainVm };
            WindowPlacement.FitAndCenter(_main, fraction: 0.92);
            mainRef = _main;
            SetupTray(desktop);

            if (!StartHidden)
            {
                _main.Show();
            }

            Instance?.ListenForActivation(
                () => Dispatcher.UIThread.Post(ShowMain),
                () => Dispatcher.UIThread.Post(async () => await QuitAsync(desktop))); // uninstaller asks us to exit
            desktop.ShutdownRequested += (_, _) => ShutdownHost();

            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                // macOS: clicking the Dock icon while the window is hidden brings it back.
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen)
                    {
                        ShowMain();
                    }
                };
            }

            _ = StartHostAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartHostAsync()
    {
        try
        {
            await _host!.StartAsync();
        }
        catch (Exception ex)
        {
            _host!.Logger.LogError(ex, "Startup failed");
        }

        Dispatcher.UIThread.Post(() => (_main?.DataContext as MainViewModel)?.RefreshStatus());
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var id = DeviceIds.Format(_host!.IncomingDeviceId);
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem($"Your address: {id}") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItemSeparator());

        var open = new NativeMenuItem("Open RemoteConnect");
        open.Click += (_, _) => ShowMain();
        menu.Items.Add(open);

        var endAll = new NativeMenuItem("End all incoming sessions");
        endAll.Click += (_, _) => _host.Host.EndAllSessions(DisconnectReason.Revoked);
        menu.Items.Add(endAll);

        menu.Items.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem("Quit RemoteConnect");
        quit.Click += async (_, _) => await QuitAsync(desktop);
        menu.Items.Add(quit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://RemoteConnect.Desktop/Assets/app-icon.png"))),
            ToolTipText = $"RemoteConnect — {id}",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void ShowMain()
    {
        if (_main is null)
        {
            return;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
        (_main.DataContext as MainViewModel)?.RefreshStatus();
    }

    private void OpenSession(ConnectTarget target)
    {
        RemoteSessionWindow? window = null;
        var vm = new RemoteSessionViewModel(_host!, target, info =>
            Dispatcher.UIThread.InvokeAsync(() => new ConfirmHostWindow(info).ShowDialog<bool>(window!)));
        window = new RemoteSessionWindow(vm);

        // Open on the same screen as the main window, maximized (like AnyDesk). "Restore" then gives a window that
        // fits that screen with its title bar visible, so it can be moved to another monitor.
        WindowPlacement.FitAndCenter(window, _main is { IsVisible: true } ? WindowPlacement.ScreenOf(_main) : null, fraction: 0.85);
        window.WindowState = WindowState.Maximized;
        window.Show();
        _ = vm.StartAsync();
    }

    private async Task CopyTextAsync(string text)
    {
        if (_main?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private async Task QuitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        if (_main is not null)
        {
            _main.AllowClose = true;
        }

        if (_tray is not null)
        {
            _tray.IsVisible = false;
        }

        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        desktop.Shutdown(0);
    }

    /// <summary>Settings → Uninstall: confirm, end sessions, remove everything and exit.</summary>
    private async Task UninstallAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_main is null || _host is null || _quitting)
        {
            return;
        }

        ShowMain();
        bool? removeData = await new UninstallWindow().ShowDialog<bool?>(_main);
        if (removeData is null)
        {
            return;
        }

        _quitting = true;
        _main.AllowClose = true;
        if (_tray is not null)
        {
            _tray.IsVisible = false;
        }

        _host.Host.EndAllSessions(DisconnectReason.HostShuttingDown);
        await _host.DisposeAsync();
        Uninstaller.Remove(removeData.Value);
        Uninstaller.DeleteProgramFiles();
        desktop.Shutdown(0);
    }

    private void ShutdownHost()
    {
        if (_quitting || _host is null)
        {
            return;
        }

        _quitting = true;
        try
        {
            _host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }
    }
}
