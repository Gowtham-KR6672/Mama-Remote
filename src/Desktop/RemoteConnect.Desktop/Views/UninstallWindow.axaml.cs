using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RemoteConnect.Desktop.Views;

/// <summary>Confirmation for Settings → Uninstall. Result: null = cancelled, otherwise "also delete user data".</summary>
public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();
    }

    private void OnUninstall(object? sender, RoutedEventArgs e) => Close((bool?)(RemoveData.IsChecked == true));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((bool?)null);
}
