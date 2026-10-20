using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using RemoteConnect.Platform;

namespace RemoteConnect.Desktop.Services;

/// <summary>Opens windows sized to the screen they appear on, so the title bar (move / minimize / close) is always reachable.</summary>
public static class WindowPlacement
{
    /// <summary>The screen showing most of <paramref name="window"/>, or the primary screen when it is hidden.</summary>
    public static Screen? ScreenOf(Window window)
    {
        if (window.IsVisible)
        {
            var size = window.FrameSize ?? window.ClientSize;
            double scale = window.DesktopScaling;
            var centre = new PixelPoint(
                window.Position.X + (int)(size.Width * scale / 2),
                window.Position.Y + (int)(size.Height * scale / 2));
            if (window.Screens.ScreenFromPoint(centre) is { } screen)
            {
                return screen;
            }
        }

        return window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
    }

    /// <summary>Call before Show(): shrink to fit <paramref name="screen"/> (default: primary) and centre on it.</summary>
    public static void FitAndCenter(Window window, Screen? screen = null, double fraction = 0.9)
    {
        screen ??= window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var b = FrameGeometry.FitWindow(
            double.IsNaN(window.Width) ? double.MaxValue : window.Width,
            double.IsNaN(window.Height) ? double.MaxValue : window.Height,
            area.X, area.Y, area.Width, area.Height, screen.Scaling, fraction);

        window.MinWidth = Math.Min(window.MinWidth, b.Width);
        window.MinHeight = Math.Min(window.MinHeight, b.Height);
        window.Width = b.Width;
        window.Height = b.Height;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(b.X, b.Y);
    }
}
