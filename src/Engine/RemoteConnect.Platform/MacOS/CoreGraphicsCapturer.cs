using System.Runtime.Versioning;
using static RemoteConnect.Platform.MacOS.MacNative;

namespace RemoteConnect.Platform.MacOS;

/// <summary>
/// macOS capture via CGDisplayCreateImage, scaled into a BGRX bitmap context. Requires the Screen Recording
/// permission (System Settings → Privacy &amp; Security). Without it macOS returns only the wallpaper.
/// ScreenCaptureKit (lower CPU, required long-term by Apple) is the planned M2 upgrade behind the same interface.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class CoreGraphicsCapturer : IScreenCapturer
{
    private readonly IntPtr _colorSpace = CGColorSpaceCreateDeviceRGB();
    private readonly Dictionary<int, uint> _displayIds = new();

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var ids = new uint[16];
        if (CGGetActiveDisplayList((uint)ids.Length, ids, out uint count) != 0 || count == 0)
        {
            return Array.Empty<DisplayInfo>();
        }

        uint main = CGMainDisplayID();
        var list = new List<(uint Id, DisplayInfo Info)>();
        for (int i = 0; i < count; i++)
        {
            uint id = ids[i];
            var b = CGDisplayBounds(id);
            int pw = (int)b.Width, ph = (int)b.Height;
            IntPtr mode = CGDisplayCopyDisplayMode(id);
            if (mode != IntPtr.Zero)
            {
                pw = (int)CGDisplayModeGetPixelWidth(mode);
                ph = (int)CGDisplayModeGetPixelHeight(mode);
                CGDisplayModeRelease(mode);
            }

            bool primary = id == main;
            list.Add((id, new DisplayInfo(0, primary ? "Main display" : $"Display {i + 1}", primary, b.X, b.Y, b.Width, b.Height, pw, ph)));
        }

        var ordered = list.OrderByDescending(d => d.Info.IsPrimary).ThenBy(d => d.Info.X).ThenBy(d => d.Info.Y).ToList();
        lock (_displayIds)
        {
            _displayIds.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                _displayIds[i] = ordered[i].Id;
            }
        }

        return ordered.Select((d, i) => d.Info with { Index = i }).ToList();
    }

    public unsafe bool Capture(DisplayInfo display, FrameBuffer target, int maxWidth, int maxHeight)
    {
        uint displayId;
        lock (_displayIds)
        {
            if (!_displayIds.TryGetValue(display.Index, out displayId))
            {
                return false;
            }
        }

        IntPtr image = CGDisplayCreateImage(displayId);
        if (image == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int iw = (int)CGImageGetWidth(image);
            int ih = (int)CGImageGetHeight(image);
            var (w, h) = FrameGeometry.Fit(iw, ih, maxWidth, maxHeight);
            if (w == 0)
            {
                return false;
            }

            target.EnsureSize(w, h);
            fixed (byte* p = target.Pixels)
            {
                IntPtr ctx = CGBitmapContextCreate((IntPtr)p, (nuint)w, (nuint)h, 8, (nuint)target.Stride, _colorSpace, BitmapInfoBgrx);
                if (ctx == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    CGContextSetInterpolationQuality(ctx, InterpolationMedium);
                    CGContextDrawImage(ctx, new CGRect(0, 0, w, h), image);
                }
                finally
                {
                    CGContextRelease(ctx);
                }
            }

            // CGDisplayCreateImage omits the cursor; draw a pointer at the current location.
            var loc = CurrentMouseLocation();
            double cx = (loc.X - display.X) / display.Width * w;
            double cy = (loc.Y - display.Y) / display.Height * h;
            CursorOverlay.Draw(target, (int)cx, (int)cy);
            return true;
        }
        finally
        {
            CGImageRelease(image);
        }
    }

    public void Dispose()
    {
        if (_colorSpace != IntPtr.Zero)
        {
            CGColorSpaceRelease(_colorSpace);
        }
    }
}
