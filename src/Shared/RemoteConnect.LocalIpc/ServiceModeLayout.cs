namespace RemoteConnect.LocalIpc;

/// <summary>
/// Fixed names and locations shared by the Windows service, its session agent, the service CLI and the desktop app.
/// None of them are configurable at run time: the service only ever starts <see cref="AgentExecutable"/> from its
/// own directory with <see cref="AgentArgument"/>.
/// </summary>
public static class ServiceModeLayout
{
    public const string ServiceName = "RemoteConnectHost";
    public const string ServiceDisplayName = "RemoteConnect Host Service";
    public const string ServiceExecutable = "RemoteConnect.HostService.exe";
    public const string AgentExecutable = "RemoteConnect.Desktop.exe";
    public const string AgentArgument = "--agent";
    public const string PipeName = "RemoteConnect.HostService.v1";

    /// <summary>HKLM key with the service's public, non-secret status (readable by every local user).</summary>
    public const string PublicStatusKey = @"SOFTWARE\RemoteConnect\Service";

    /// <summary>HKLM key the Service Control Manager creates for the installed service.</summary>
    public const string ServiceRegistryKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;

    /// <summary>
    /// Machine-wide data of the service-mode host (device key, settings with the password hash, logs). It lives in the
    /// LocalSystem profile, which only SYSTEM and Administrators can open, so a standard user can neither read the
    /// secrets nor pre-create ("squat") the folder before the service does.
    /// </summary>
    public static string DataDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "config", "systemprofile", "AppData", "Local", "RemoteConnect");

    public static string SettingsFile => Path.Combine(DataDirectory, "service-settings.json");

    public static string RegistrationKeyFile => Path.Combine(DataDirectory, "registration.key");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string AgentPath(string installDirectory) => Path.Combine(installDirectory, AgentExecutable);

    public static string ServicePath(string installDirectory) => Path.Combine(installDirectory, ServiceExecutable);

    public static bool SamePath(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
