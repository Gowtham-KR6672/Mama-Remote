using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteConnect.Desktop.Services;
using RemoteConnect.Platform;
using RemoteConnect.Protocol;
using RemoteConnect.Security;
using RemoteConnect.Session.Signaling;

namespace RemoteConnect.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _app;

    public SettingsViewModel(AppHost app)
    {
        _app = app;
        Load();
    }

    public IReadOnlyList<string> Themes { get; } = new[] { "System", "Light", "Dark" };

    public IReadOnlyList<QualityPreset> Qualities { get; } = Enum.GetValues<QualityPreset>();

    public bool AutoStartSupported => PlatformServices.AutoStart.IsSupported;

    public string DeviceId => DeviceIds.Format(_app.Identity.DeviceId);

    public string Fingerprint => CertificateFingerprint.ToDisplay(_app.Identity.Fingerprint, 16);

    public string ConfigFolder => AppPaths.ConfigDirectory;

    public string Version => $"RemoteConnect {AppHost.AppVersion} · {PlatformServices.PlatformName}";

    [ObservableProperty] private string _alias = string.Empty;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private string _serverUrl = string.Empty;
    [ObservableProperty] private string _registrationKey = string.Empty;
    [ObservableProperty] private bool _allowIncoming;
    [ObservableProperty] private bool _allowControl;
    [ObservableProperty] private bool _allowClipboard;
    [ObservableProperty] private bool _unattendedEnabled;
    [ObservableProperty] private bool _unattendedAllowControl;
    [ObservableProperty] private string _unattendedAllowList = string.Empty;
    [ObservableProperty] private string _newPassword = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private bool _hasPassword;
    [ObservableProperty] private bool _lanListenerEnabled;
    [ObservableProperty] private decimal? _lanPort;
    [ObservableProperty] private bool _lanDiscoveryEnabled;
    [ObservableProperty] private QualityPreset _quality;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    public string PasswordStatus => HasPassword ? "A password is set." : "No password set — unattended access cannot be used yet.";

    partial void OnHasPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordStatus));

    private void Load()
    {
        var s = _app.Settings;
        Alias = s.Alias;
        Theme = s.Theme;
        AutoStart = s.AutoStart;
        ServerUrl = s.ServerUrl ?? string.Empty;
        RegistrationKey = _app.Store.RegistrationKey ?? string.Empty;
        AllowIncoming = s.AllowIncoming;
        AllowControl = s.AllowControl;
        AllowClipboard = s.AllowClipboard;
        UnattendedEnabled = s.UnattendedEnabled;
        UnattendedAllowControl = s.UnattendedAllowControl;
        UnattendedAllowList = string.Join(", ", s.UnattendedAllowList.Select(DeviceIds.Format));
        HasPassword = !string.IsNullOrEmpty(s.UnattendedPasswordHash);
        LanListenerEnabled = s.LanListenerEnabled;
        LanPort = s.LanPort;
        LanDiscoveryEnabled = s.LanDiscoveryEnabled;
        Quality = s.Quality;
    }

    [RelayCommand]
    private void SetPassword()
    {
        if (NewPassword != ConfirmPassword)
        {
            Show("The passwords do not match.", error: true);
            return;
        }

        try
        {
            PasswordHasher.ValidateStrength(NewPassword);
        }
        catch (ArgumentException ex)
        {
            Show(ex.Message, error: true);
            return;
        }

        _app.Settings.UnattendedPasswordHash = PasswordHasher.Hash(NewPassword);
        NewPassword = ConfirmPassword = string.Empty;
        HasPassword = true;
        _app.Store.TrySave();
        Show("Unattended-access password saved. Enable unattended access below and click Save.", error: false);
    }

    [RelayCommand]
    private void ClearPassword()
    {
        _app.Settings.UnattendedPasswordHash = null;
        _app.Settings.UnattendedEnabled = false;
        UnattendedEnabled = false;
        HasPassword = false;
        _app.Store.TrySave();
        Show("Password removed and unattended access disabled.", error: false);
    }

    [RelayCommand]
    private async Task Save()
    {
        if (string.IsNullOrWhiteSpace(Alias) || Alias.Length > 64)
        {
            Show("Enter a display name (up to 64 characters).", error: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(ServerUrl) && !ServerAddress.TryParse(ServerUrl, out _, out var serverError))
        {
            Show(serverError, error: true);
            return;
        }

        int port = (int)(LanPort ?? 0);
        if (port is < 1024 or > 65535)
        {
            Show("Port must be between 1024 and 65535.", error: true);
            return;
        }

        var allow = new List<string>();
        foreach (var part in Regex.Split(UnattendedAllowList ?? string.Empty, @"[,;\r\n]+"))
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (!DeviceIds.TryNormalize(part, out var id))
            {
                Show($"'{part.Trim()}' is not a valid 9-digit ID.", error: true);
                return;
            }

            allow.Add(id);
        }

        if (UnattendedEnabled && !HasPassword)
        {
            Show("Set a password before enabling unattended access.", error: true);
            return;
        }

        var s = _app.Settings;
        bool autoStartChanged = s.AutoStart != AutoStart;
        s.Alias = Alias.Trim();
        s.Theme = Theme;
        s.AutoStart = AutoStart;
        s.ServerUrl = string.IsNullOrWhiteSpace(ServerUrl) ? null : ServerUrl.Trim();
        s.AllowIncoming = AllowIncoming;
        s.AllowControl = AllowControl;
        s.AllowClipboard = AllowClipboard;
        s.UnattendedEnabled = UnattendedEnabled;
        s.UnattendedAllowControl = UnattendedAllowControl;
        s.UnattendedAllowList = allow.Distinct().ToList();
        s.LanListenerEnabled = LanListenerEnabled;
        s.LanPort = port;
        s.LanDiscoveryEnabled = LanDiscoveryEnabled;
        s.Quality = Quality;
        _app.Store.RegistrationKey = string.IsNullOrWhiteSpace(RegistrationKey) ? null : RegistrationKey.Trim();

        try
        {
            _app.Store.Save();
        }
        catch (IOException ex)
        {
            Show("Could not save settings: " + ex.Message, error: true);
            return;
        }

        if (autoStartChanged)
        {
            _app.ApplyAutoStart();
        }

        ApplyTheme(Theme);
        await _app.ApplyNetworkSettingsAsync();
        Show("Settings saved.", error: false);
    }

    [RelayCommand]
    private void OpenConfigFolder() => PlatformServices.OpenUrl(new Uri(AppPaths.ConfigDirectory).AbsoluteUri);

    public static void ApplyTheme(string theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    private void Show(string message, bool error)
    {
        Message = message;
        MessageIsError = error;
    }
}
