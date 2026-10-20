using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteConnect.Desktop.ViewModels;
using RemoteConnect.Platform;

namespace RemoteConnect.Desktop.Views;

public partial class RemoteSessionWindow : Window
{
    private bool _disposed;

    public RemoteSessionWindow()
    {
        InitializeComponent();
    }

    public RemoteSessionWindow(RemoteSessionViewModel vm)
        : this()
    {
        DataContext = vm;
        vm.CloseRequested += Close;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemoteSessionViewModel.IsConnected) && vm.IsConnected)
            {
                Screen.Focus();
            }
        };
    }

    private void SendTaskManager(object? sender, RoutedEventArgs e)
    {
        // Ctrl+Shift+Esc opens Task Manager on a Windows host. (Ctrl+Alt+Del is reserved by Windows and cannot be injected.)
        Screen.SendChord(KeyMap.LeftCtrl, KeyMap.LeftShift, 0x29);
        Screen.Focus();
    }

    private WindowState _stateBeforeFullScreen = WindowState.Maximized;

    private void ToggleFullScreen(object? sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullScreen;
        }
        else
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }

        FullScreenToggle.IsChecked = WindowState == WindowState.FullScreen;
        Screen.Focus();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!_disposed && DataContext is RemoteSessionViewModel vm)
        {
            _disposed = true;
            Screen.ReleaseKeys();
            await vm.DisposeAsync();
        }
    }
}
