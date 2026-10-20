using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnect.Protocol;
using RemoteConnect.Security;
using RemoteConnect.Session.Host;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.Desktop.ViewModels;

public sealed partial class IncomingRequestViewModel : ObservableObject
{
    private readonly IncomingRequest _request;

    public IncomingRequestViewModel(IncomingRequest request, bool allowControl)
    {
        _request = request;
        bool control = allowControl && request.Requested.HasFlag(SessionPermissions.ControlInput);
        PermissionsText = control
            ? "See your screen and control your mouse and keyboard" + (request.Requested.HasFlag(SessionPermissions.Clipboard) ? ", and share the clipboard." : ".")
            : "See your screen (view only).";
        Warning = control
            ? "Only accept if you know this person and expected this connection. They will be able to do anything you can do on this computer."
            : "Only accept if you know this person and expected this connection.";
        SecondsLeft = Math.Max(1, (int)(request.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
    }

    public string DisplayName => _request.Alias;

    public string DeviceId => DeviceIds.Format(_request.DeviceId);

    public string Platform => string.IsNullOrEmpty(_request.Platform) ? "Unknown OS" : _request.Platform;

    public string Fingerprint => CertificateFingerprint.ToDisplay(_request.Fingerprint);

    public string Route => _request.Path == ConnectionPath.Direct
        ? $"Directly from {_request.RemoteEndpoint}"
        : "Through your RemoteConnect server (end-to-end encrypted)";

    public string PermissionsText { get; }

    public string Warning { get; }

    public bool UnattendedNote => _request.UnattendedAvailable;

    public TaskCompletionSource<ApprovalDecision> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [ObservableProperty]
    private int _secondsLeft;

    public string CountdownText => $"Automatically declined in {SecondsLeft} s";

    partial void OnSecondsLeftChanged(int value)
    {
        OnPropertyChanged(nameof(CountdownText));
        if (value <= 0)
        {
            Result.TrySetResult(ApprovalDecision.Reject);
        }
    }

    [RelayCommand]
    private void Accept() => Result.TrySetResult(ApprovalDecision.Accept);

    [RelayCommand]
    private void ViewOnly() => Result.TrySetResult(ApprovalDecision.ViewOnly);

    [RelayCommand]
    private void Reject() => Result.TrySetResult(ApprovalDecision.Reject);
}
