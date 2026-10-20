using Avalonia.Threading;
using RemoteConnect.Desktop.ViewModels;
using RemoteConnect.Desktop.Views;
using RemoteConnect.Protocol;
using RemoteConnect.Session.Host;

namespace RemoteConnect.Desktop.Services;

/// <summary>UI side of hosting: the Accept/Reject prompt and the visible "session active" panel.</summary>
public sealed class HostInteraction : IHostInteraction
{
    private readonly Dictionary<string, HostIndicatorWindow> _indicators = new();
    private AppHost? _app;

    public void Attach(AppHost app) => _app = app;

    public async Task<ApprovalDecision> RequestApprovalAsync(IncomingRequest request, CancellationToken ct)
    {
        // InvokeAsync unwraps the returned task: this completes when the user decides (or the request expires).
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var vm = new IncomingRequestViewModel(request, _app?.Settings.AllowControl ?? false);
            var window = new IncomingRequestWindow(vm);
            window.Show();
            window.Activate();
            ct.Register(() => vm.Result.TrySetResult(ApprovalDecision.Reject)); // timeout / requester left → window closes
            return vm.Result.Task;
        });
    }

    public void OnSessionStarted(HostSession session)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var window = new HostIndicatorWindow(new HostIndicatorViewModel(session), _indicators.Count);
            _indicators[session.Id] = window;
            window.Show();
        });

        _app?.RecordRecent(new RecentSession(
            "Incoming",
            session.ControllerId,
            session.ControllerId,
            session.ControllerAlias,
            DateTimeOffset.Now,
            session.Path.ToString(),
            session.IsUnattended ? "Unattended login" : "Accepted"));
    }

    public void OnSessionEnded(HostSession session, DisconnectReason reason, string? detail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_indicators.Remove(session.Id, out var window))
            {
                window.AllowClose = true;
                window.Close();
            }
        });
    }
}
