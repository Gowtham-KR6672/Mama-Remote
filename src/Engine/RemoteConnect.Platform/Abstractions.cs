using RemoteConnect.Protocol;

namespace RemoteConnect.Platform;

/// <summary>
/// A physical display. <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/> are in the platform's
/// input coordinate space (Windows: physical pixels of the virtual desktop; macOS: global points).
/// </summary>
public sealed record DisplayInfo(
    int Index,
    string Name,
    bool IsPrimary,
    double X,
    double Y,
    double Width,
    double Height,
    int PixelWidth,
    int PixelHeight);

/// <summary>Reusable 32-bit BGRA pixel buffer (top-down rows).</summary>
public sealed class FrameBuffer
{
    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; private set; } = Array.Empty<byte>();

    public bool EnsureSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        if (width == Width && height == Height)
        {
            return false;
        }

        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        return true;
    }
}

public interface IScreenCapturer : IDisposable
{
    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>
    /// Captures <paramref name="display"/> (with the mouse cursor) into <paramref name="target"/>, downscaled to fit
    /// within <paramref name="maxWidth"/> x <paramref name="maxHeight"/>. Returns false when the desktop cannot be
    /// captured right now (secure desktop, locked session, missing permission).
    /// </summary>
    bool Capture(DisplayInfo display, FrameBuffer target, int maxWidth, int maxHeight);
}

/// <summary>Injects input on the host. Coordinates are normalized (0..1) to the given display.</summary>
public interface IInputInjector : IDisposable
{
    void MovePointer(DisplayInfo display, double nx, double ny);

    void PointerButton(DisplayInfo display, double nx, double ny, MouseButton button, bool down);

    /// <summary>Deltas in Windows wheel units (120 per notch); positive Y scrolls up.</summary>
    void Scroll(DisplayInfo display, double nx, double ny, int deltaX, int deltaY);

    void Key(ushort hidUsage, bool down);

    /// <summary>Releases every key and button this injector pressed (called on disconnect to avoid stuck keys).</summary>
    void ReleaseAll();
}

public sealed record PermissionStatus(bool ScreenCaptureGranted, bool InputControlGranted, string? Hint);

public interface IAutoStart
{
    bool IsSupported { get; }

    bool IsEnabled();

    void SetEnabled(bool enabled);
}

public static class FrameGeometry
{
    /// <summary>Largest size with the same aspect ratio that fits in max bounds (never upscales).</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        double scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        int w = Math.Max(1, (int)Math.Round(width * scale));
        int h = Math.Max(1, (int)Math.Round(height * scale));
        // Even dimensions keep JPEG chroma subsampling aligned.
        if (w > 1 && (w & 1) == 1) w--;
        if (h > 1 && (h & 1) == 1) h--;
        return (w, h);
    }

    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>Maps a normalized position to an absolute point inside the display (edges inclusive).</summary>
    public static (double X, double Y) ToAbsolute(DisplayInfo display, double nx, double ny)
        => (display.X + (Clamp01(nx) * Math.Max(0, display.Width - 1)),
            display.Y + (Clamp01(ny) * Math.Max(0, display.Height - 1)));
}
