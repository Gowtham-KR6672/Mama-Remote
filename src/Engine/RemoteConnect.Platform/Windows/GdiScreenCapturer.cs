using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static RemoteConnect.Platform.Windows.Win32;

namespace RemoteConnect.Platform.Windows;

/// <summary>
/// Windows capture via GDI BitBlt/StretchBlt from the desktop DC. Works on every GPU/driver and in VMs.
/// DXGI Desktop Duplication (GPU path, dirty-rect metadata) is the planned M2 upgrade behind the same interface.
/// The thread is made per-monitor DPI aware so all coordinates are physical pixels.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapturer : IScreenCapturer
{
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _dibWidth;
    private int _dibHeight;

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var result = new List<DisplayInfo>();
            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var r = info.rcMonitor;
                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    bool primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                    string name = string.IsNullOrWhiteSpace(info.szDevice) ? $"Display {result.Count + 1}" : info.szDevice.Replace(@"\\.\", string.Empty);
                    result.Add(new DisplayInfo(result.Count, name, primary, r.Left, r.Top, w, h, w, h));
                }

                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            // Primary first, then left-to-right, so index 0 is the natural default.
            var ordered = result.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.X).ThenBy(d => d.Y).ToList();
            return ordered.Select((d, i) => d with { Index = i, Name = d.IsPrimary ? $"{d.Name} (primary)" : d.Name }).ToList();
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    public bool Capture(DisplayInfo display, FrameBuffer target, int maxWidth, int maxHeight)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            int srcX = (int)display.X, srcY = (int)display.Y;
            int srcW = (int)display.Width, srcH = (int)display.Height;
            var (dstW, dstH) = FrameGeometry.Fit(srcW, srcH, maxWidth, maxHeight);
            if (dstW == 0)
            {
                return false;
            }

            EnsureDib(dstW, dstH);
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                bool ok;
                if (dstW == srcW && dstH == srcH)
                {
                    ok = BitBlt(_memDc, 0, 0, dstW, dstH, screen, srcX, srcY, SRCCOPY);
                }
                else
                {
                    SetStretchBltMode(_memDc, HALFTONE);
                    SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
                    ok = StretchBlt(_memDc, 0, 0, dstW, dstH, screen, srcX, srcY, srcW, srcH, SRCCOPY);
                }

                if (!ok)
                {
                    // Typically the secure desktop (UAC / lock screen) is active.
                    return false;
                }

                DrawCursor(srcX, srcY, (double)dstW / srcW, (double)dstH / srcH);
                GdiFlush();
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }

            target.EnsureSize(dstW, dstH);
            Marshal.Copy(_bits, target.Pixels, 0, dstW * dstH * 4);
            return true;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    private void DrawCursor(int originX, int originY, double scaleX, double scaleY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
        {
            return;
        }

        int hotX = 0, hotY = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hotX = ii.xHotspot;
            hotY = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }

        int x = (int)((ci.ptScreenPos.X - originX) * scaleX) - hotX;
        int y = (int)((ci.ptScreenPos.Y - originY) * scaleY) - hotY;
        DrawIconEx(_memDc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    private void EnsureDib(int width, int height)
    {
        if (_dib != IntPtr.Zero && width == _dibWidth && height == _dibHeight)
        {
            return;
        }

        ReleaseDib();
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };
            _dib = CreateDIBSection(screen, ref bmi, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            _oldBitmap = SelectObject(_memDc, _dib);
            _dibWidth = width;
            _dibHeight = height;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void ReleaseDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero)
            {
                SelectObject(_memDc, _oldBitmap);
            }

            DeleteDC(_memDc);
        }

        if (_dib != IntPtr.Zero)
        {
            DeleteObject(_dib);
        }

        _memDc = _dib = _oldBitmap = _bits = IntPtr.Zero;
        _dibWidth = _dibHeight = 0;
    }

    public void Dispose() => ReleaseDib();
}
