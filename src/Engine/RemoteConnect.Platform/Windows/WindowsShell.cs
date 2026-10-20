using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RemoteConnect.Platform.Windows;

/// <summary>Windows shell integration: "Installed apps" entry, capture exclusion and simple message boxes.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsShell
{
    public const string UninstallArgument = "--uninstall";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteConnect";
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>
    /// Lists this copy of RemoteConnect under Settings → Apps → Installed apps (and Control Panel → Programs),
    /// so a copy that was simply run as a single file can be uninstalled like any other app.
    /// An entry written by the install script is left alone (its uninstaller removes more).
    /// </summary>
    public static void RegisterInstalledApp(string exePath, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        if (key.GetValue("UninstallString") is string existing
            && existing.Contains("uninstall-windows.ps1", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        key.SetValue("DisplayName", "RemoteConnect");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "RemoteConnect");
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("InstallLocation", Path.GetDirectoryName(exePath) ?? string.Empty);
        key.SetValue("UninstallString", $"\"{exePath}\" {UninstallArgument}");
        key.SetValue("QuietUninstallString", $"\"{exePath}\" {UninstallArgument} --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try
        {
            key.SetValue("EstimatedSize", (int)(new FileInfo(exePath).Length / 1024), RegistryValueKind.DWord);
        }
        catch (IOException)
        {
        }
    }

    public static void UnregisterInstalledApp()
        => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

    /// <summary>
    /// Keeps a window out of screen capture (Windows 10 2004+): the host still sees it, the remote side does not get
    /// it painted over the shared screen. Returns false on older Windows (the window then stays capturable).
    /// </summary>
    public static bool ExcludeFromCapture(IntPtr hwnd) => hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

    public static bool IsExcludedFromCapture(IntPtr hwnd)
        => GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;

    public static bool Confirm(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, 0x4 /* YESNO */ | 0x20 /* ICONQUESTION */ | 0x10000 /* SETFOREGROUND */) == 6 /* IDYES */;

    public static void Inform(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, 0x40 /* ICONINFORMATION */ | 0x10000);

    /// <summary>Deletes a file or folder a few seconds after this process exits (a running exe cannot delete itself).</summary>
    public static void DeleteAfterExit(string path, bool isDirectory)
    {
        var command = isDirectory ? $"rmdir /s /q \"{path}\"" : $"del /f /q \"{path}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & {command}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
