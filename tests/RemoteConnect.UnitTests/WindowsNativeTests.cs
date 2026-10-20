using System.Runtime.InteropServices;
using RemoteConnect.Platform;
using RemoteConnect.Platform.Windows;

namespace RemoteConnect.UnitTests;

public class WindowsNativeTests
{
    [Fact]
    public void SendInput_structure_has_the_native_x64_layout()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return;
        }

        // SendInput silently fails if cbSize does not match the native INPUT size (40 bytes on x64).
        Assert.Equal(40, Marshal.SizeOf<Win32.INPUT>());
        Assert.Equal(32, Marshal.SizeOf<Win32.MOUSEINPUT>());
    }

    /// <summary>
    /// Captures one real frame from this PC's primary display, in memory only. Opt-in because it reads the screen:
    /// set RC_TEST_REAL_CAPTURE=1 to run it.
    /// </summary>
    [Fact]
    public void Gdi_capture_returns_a_real_frame_when_enabled()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("RC_TEST_REAL_CAPTURE") != "1")
        {
            return;
        }

        using var capturer = new GdiScreenCapturer();
        var displays = capturer.GetDisplays();
        Assert.NotEmpty(displays);
        Assert.True(displays[0].IsPrimary);

        var frame = new FrameBuffer();
        Assert.True(capturer.Capture(displays[0], frame, 1920, 1080));
        Assert.InRange(frame.Width, 2, 1920);
        Assert.InRange(frame.Height, 2, 1080);

        long nonBlack = 0;
        for (int i = 0; i < frame.Pixels.Length; i += 4 * 97)
        {
            if (frame.Pixels[i] + frame.Pixels[i + 1] + frame.Pixels[i + 2] > 0)
            {
                nonBlack++;
            }
        }

        Assert.True(nonBlack > 0, "captured frame is entirely black");
        Array.Clear(frame.Pixels);
    }
}
