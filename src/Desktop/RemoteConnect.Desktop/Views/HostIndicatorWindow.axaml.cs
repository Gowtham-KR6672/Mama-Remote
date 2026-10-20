using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using RemoteConnect.Desktop.ViewModels;
using RemoteConnect.Platform.Windows;

namespace RemoteConnect.Desktop.Views;

/// <summary>
/// Small always-on-top panel shown on the host for as long as a session is active. It sits in the bottom-right
/// corner (draggable), can be shrunk to a bar, and is kept out of the shared screen image so it never covers what
/// the remote side is looking at.
/// </summary>
public partial class HostIndicatorWindow : Window
{
    private readonly DispatcherTimer? _timer;
    private readonly int _stackIndex;
    private Size _anchoredSize;
    private bool _userMoved;

    public HostIndicatorWindow()
    {
        InitializeComponent();
        DragArea.PointerPressed += OnDragPressed;
        DragAreaCollapsed.PointerPressed += OnDragPressed;
    }

    public HostIndicatorWindow(HostIndicatorViewModel vm, int stackIndex)
        : this()
    {
        DataContext = vm;
        _stackIndex = stackIndex;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => vm.Tick());
        _timer.Start();
        Opened += (_, _) =>
        {
            ExcludeFromScreenCapture();
            AnchorBottomRight();
        };

        // Size changes after the first layout and on every shrink/expand: stay in the corner unless the user moved it.
        LayoutUpdated += (_, _) =>
        {
            if (!_userMoved && ClientSize != _anchoredSize)
            {
                AnchorBottomRight();
            }
        };
    }

    /// <summary>The panel ends when the session ends, never by closing it independently.</summary>
    public bool AllowClose { get; set; }

    /// <summary>True when the panel is hidden from screen capture (Windows 10 2004 or later).</summary>
    public bool ExcludedFromCapture { get; private set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        base.OnClosed(e);
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _userMoved = true;
            BeginMoveDrag(e);
        }
    }

    private void ExcludeFromScreenCapture()
    {
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is { } hwnd)
        {
            ExcludedFromCapture = WindowsShell.ExcludeFromCapture(hwnd);
        }
    }

    private void AnchorBottomRight()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        var size = ClientSize;
        if (screen is null || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        _anchoredSize = size;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int width = (int)Math.Ceiling(size.Width * scale);
        int height = (int)Math.Ceiling(size.Height * scale);
        int margin = (int)(12 * scale);
        Position = new PixelPoint(
            area.Right - width - margin,
            area.Bottom - height - margin - (_stackIndex * (height + margin)));
    }
}
