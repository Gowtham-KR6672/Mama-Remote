using Avalonia.Controls;
using Avalonia.Threading;
using RemoteConnect.Desktop.ViewModels;

namespace RemoteConnect.Desktop.Views;

public partial class IncomingRequestWindow : Window
{
    private readonly DispatcherTimer? _timer;

    public IncomingRequestWindow()
    {
        InitializeComponent();
    }

    public IncomingRequestWindow(IncomingRequestViewModel vm)
        : this()
    {
        DataContext = vm;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => vm.SecondsLeft--);
        _timer.Start();
        _ = vm.Result.Task.ContinueWith(_ => Dispatcher.UIThread.Post(Close), TaskScheduler.Default);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        if (DataContext is IncomingRequestViewModel vm)
        {
            vm.Result.TrySetResult(Session.Host.ApprovalDecision.Reject); // closing the window = reject
        }

        base.OnClosed(e);
    }
}
