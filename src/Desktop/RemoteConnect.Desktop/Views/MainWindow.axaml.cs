using Avalonia.Controls;

namespace RemoteConnect.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Set when the user chose Quit; otherwise closing the window only hides it to the tray.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose)
        {
            // Like AnyDesk: the window closes but RemoteConnect keeps running in the tray, ready for connections.
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    public void FocusAddress() => AddressBox.Focus();
}
