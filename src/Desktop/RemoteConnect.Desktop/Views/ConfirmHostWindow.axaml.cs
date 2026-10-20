using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteConnect.Security;
using RemoteConnect.Session.Controller;

namespace RemoteConnect.Desktop.Views;

/// <summary>Trust-on-first-use confirmation when connecting by IP address to a device seen for the first time.</summary>
public partial class ConfirmHostWindow : Window
{
    public ConfirmHostWindow()
    {
        InitializeComponent();
    }

    public ConfirmHostWindow(UnknownHostInfo info)
        : this()
    {
        TargetText.Text = $"You are connecting to {info.Target}. The device there identifies itself as:";
        IdText.Text = "ID " + DeviceIds.Format(info.DeviceId);
        FingerprintText.Text = "Key fingerprint: " + CertificateFingerprint.ToDisplay(info.Fingerprint, 16);
    }

    private void OnTrust(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
