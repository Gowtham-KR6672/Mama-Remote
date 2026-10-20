using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RemoteConnect.Platform.Windows;

/// <summary>
/// Per-user start at sign-in via HKCU\...\Run (visible in Task Manager → Startup apps, where the user can disable it).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStart : IAutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RemoteConnect";

    public bool IsSupported => true;

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string s && s.Contains(LaunchCommand.ExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, LaunchCommand.ForWindowsRunKey(), RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
