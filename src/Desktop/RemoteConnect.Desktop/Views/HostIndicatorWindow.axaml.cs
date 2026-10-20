using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using RemoteConnect.Desktop.ViewModels;

namespace RemoteConnect.Desktop.Views;

/// <summary>Small always-on-top panel shown on the host for as long as a session is active.</summary>
public partial class HostIndicatorWindow : Window
{
    private readonly DispatcherTimer? _timer;

    public HostIndicatorWindow()
    {
        InitializeComponent();
        DragArea.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };
    }

    public HostIndicatorWindow(HostIndicatorViewModel vm, int stackIndex)
        : this()
    {
        DataContext = vm;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => vm.Tick());
        _timer.Start();
        Opened += (_, _) => PlaceBottomRight(stackIndex);
    }

    /// <summary>The panel ends when the session ends, never by closing it independently.</summary>
    public bool AllowClose { get; set; }

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

    private void PlaceBottomRight(int stackIndex)
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int width = (int)(Bounds.Width * scale);
        int height = (int)(Bounds.Height * scale);
        int margin = (int)(16 * scale);
        Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin - (stackIndex * (height + margin)));
    }
}
