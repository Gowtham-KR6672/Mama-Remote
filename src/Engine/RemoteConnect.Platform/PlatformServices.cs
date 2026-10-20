using System.Diagnostics;
using System.Runtime.Versioning;
using RemoteConnect.Platform.MacOS;
using RemoteConnect.Platform.Windows;

namespace RemoteConnect.Platform;

/// <summary>Factory for the current operating system's implementations.</summary>
public static class PlatformServices
{
    public static string PlatformName
        => OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux"
            : "Unknown";

    public static bool CanHost => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static IScreenCapturer CreateCapturer()
    {
        if (OperatingSystem.IsWindows())
        {
            return new GdiScreenCapturer();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new CoreGraphicsCapturer();
        }

        throw new PlatformNotSupportedException("Screen capture is implemented for Windows and macOS.");
    }

    public static IInputInjector CreateInjector()
    {
        if (OperatingSystem.IsWindows())
        {
            return new SendInputInjector();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new CGEventInjector();
        }

        throw new PlatformNotSupportedException("Input injection is implemented for Windows and macOS.");
    }

    /// <summary>
    /// Service mode (Windows): capture that follows the input desktop, so the sign-in, lock and UAC screens are visible
    /// to the LocalSystem session agent.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IScreenCapturer CreateInputDesktopCapturer() => new InputDesktopCapturer(new GdiScreenCapturer());

    /// <summary>Service mode (Windows): input that follows the input desktop (see <see cref="CreateInputDesktopCapturer"/>).</summary>
    [SupportedOSPlatform("windows")]
    public static IInputInjector CreateInputDesktopInjector() => new InputDesktopInjector(new SendInputInjector());

    public static IAutoStart AutoStart { get; } = CreateAutoStart();

    private static IAutoStart CreateAutoStart()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsAutoStart();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacAutoStart();
        }

        return new UnsupportedAutoStart();
    }

    public static PermissionStatus CheckPermissions()
    {
        if (OperatingSystem.IsMacOS())
        {
            bool screen = MacNative.CGPreflightScreenCaptureAccess();
            bool input = MacNative.AXIsProcessTrusted();
            string? hint = (screen, input) switch
            {
                (true, true) => null,
                (false, true) => "Allow Screen Recording for RemoteConnect in System Settings → Privacy & Security.",
                (true, false) => "Allow Accessibility for RemoteConnect in System Settings → Privacy & Security so remote control works.",
                _ => "Allow Screen Recording and Accessibility for RemoteConnect in System Settings → Privacy & Security.",
            };
            return new PermissionStatus(screen, input, hint);
        }

        return new PermissionStatus(CanHost, CanHost, CanHost ? null : "Hosting is not supported on this platform.");
    }

    /// <summary>Triggers the OS permission prompts (macOS) and opens the relevant settings pane.</summary>
    public static void RequestPermissions()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (!MacNative.CGPreflightScreenCaptureAccess())
        {
            MacNative.CGRequestScreenCaptureAccess();
            OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");
        }

        if (!MacNative.AXIsProcessTrusted())
        {
            MacNative.PromptAccessibility();
            OpenUrl("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // Opening a settings pane is a convenience only.
        }
    }

    private sealed class UnsupportedAutoStart : IAutoStart
    {
        public bool IsSupported => false;

        public bool IsEnabled() => false;

        public void SetEnabled(bool enabled)
        {
        }
    }
}
