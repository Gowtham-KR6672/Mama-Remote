namespace RemoteConnect.Desktop.Services;

/// <summary>
/// Per-user data locations. Windows: %APPDATA%\RemoteConnect. macOS: ~/Library/Application Support/RemoteConnect.
/// The service-mode agent switches to the machine-wide directory before anything is read (<see cref="UseDirectory"/>).
/// </summary>
public static class AppPaths
{
    private static string? _configDirectory;

    public static string ConfigDirectory => _configDirectory ??= Resolve();

    /// <summary>Must be called before any other member is used (service-mode agent only).</summary>
    public static void UseDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        _configDirectory = directory;
    }

    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    public static string IdentityFile => Path.Combine(ConfigDirectory, OperatingSystem.IsWindows() ? "identity.dpapi" : "identity.p12");

    public static string RegistrationKeyFile => Path.Combine(ConfigDirectory, "registration.key");

    public static string LogDirectory => Path.Combine(ConfigDirectory, "logs");

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("REMOTECONNECT_HOME");
        string dir = !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "RemoteConnect")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteConnect");
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dir;
    }
}
