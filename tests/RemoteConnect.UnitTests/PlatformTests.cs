using RemoteConnect.Platform;
using RemoteConnect.Platform.Windows;

namespace RemoteConnect.UnitTests;

public class PlatformTests
{
    [Fact]
    public void Every_key_has_a_windows_mapping()
    {
        Assert.True(KeyMap.All.Count > 100);
        Assert.All(KeyMap.All, k => Assert.True(k.WinScan != 0 || k.WinVk != 0, k.Name));
    }

    [Fact]
    public void Mac_key_codes_are_unique_except_documented_aliases()
    {
        // NonUSHash shares the backslash key, Lang1 is the JIS Kana key, and PC-only keys map to F13–F15 like Apple keyboards.
        var aliases = new HashSet<string> { "NonUSHash", "Lang1", "PrintScreen", "ScrollLock", "Pause" };
        var dupes = KeyMap.All.Where(k => k.MacKey != KeyMap.NoMacKey && !aliases.Contains(k.Name))
            .GroupBy(k => k.MacKey).Where(g => g.Count() > 1).Select(g => string.Join("/", g.Select(k => k.Name))).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void Letters_and_modifiers_map_correctly()
    {
        Assert.True(KeyMap.TryGet(0x04, out var a));
        Assert.Equal("A", a.Name);
        Assert.Equal(0x1E, a.WinScan);
        Assert.Equal(0x00, a.MacKey);

        Assert.True(KeyMap.TryGet(KeyMap.LeftMeta, out var meta));
        Assert.True(meta.WinExtended);
        Assert.Equal(0x37, meta.MacKey); // Command

        Assert.True(KeyMap.TryGet(0x1E, out var one));
        Assert.Equal("Digit1", one.Name);
    }

    [Fact]
    public void Virtual_desktop_normalization_handles_negative_origin_monitors()
    {
        // Two 1920x1080 monitors: secondary on the left at x = -1920.
        var (x0, _) = VirtualDeskMath.Normalize(-1920, 0, -1920, 0, 3840, 1080);
        var (xMid, _) = VirtualDeskMath.Normalize(0, 0, -1920, 0, 3840, 1080);
        var (xEnd, yEnd) = VirtualDeskMath.Normalize(1919, 1079, -1920, 0, 3840, 1080);
        Assert.Equal(0, x0);
        Assert.Equal(32776, xMid); // 1920 * 65535 / 3839, rounded
        Assert.Equal(65535, xEnd);
        Assert.Equal(65535, yEnd);
    }

    [Fact]
    public void Normalized_display_coordinates_map_inside_the_display()
    {
        var d = new DisplayInfo(1, "Left", false, -1920, 0, 1920, 1080, 1920, 1080);
        Assert.Equal((-1920d, 0d), FrameGeometry.ToAbsolute(d, 0, 0));
        Assert.Equal((-1d, 1079d), FrameGeometry.ToAbsolute(d, 1, 1));
        Assert.Equal((-1920d, 0d), FrameGeometry.ToAbsolute(d, -5, -5)); // clamped
    }

    [Theory]
    // The reported case: 1280x820 session window on a 1366x768 laptop (720 px working area) at 100 %.
    [InlineData(1280, 820, 0, 0, 1366, 720, 1.0)]
    // Secondary 1366x768 to the left of the primary (negative origin), and the 2560x1440 primary.
    [InlineData(1280, 820, -1366, 0, 1366, 720, 1.0)]
    [InlineData(1280, 820, 0, 0, 2560, 1392, 1.0)]
    // 1920x1080 laptop at 150 % and a 1366x768 laptop at 125 %.
    [InlineData(1280, 820, 0, 0, 1920, 1032, 1.5)]
    [InlineData(1000, 680, 0, 0, 1366, 728, 1.25)]
    public void Windows_fit_the_screen_with_their_title_bar_visible(double w, double h, int ax, int ay, int aw, int ah, double scale)
    {
        var b = FrameGeometry.FitWindow(w, h, ax, ay, aw, ah, scale);
        int outerW = (int)Math.Round(b.Width * scale);
        int outerH = (int)Math.Round((b.Height + 40) * scale);
        Assert.InRange(b.X, ax, ax + aw - outerW);       // fully inside horizontally
        Assert.InRange(b.Y, ay, ay + ah - outerH);       // title bar (top) and bottom edge on screen
        Assert.True(b.Width <= w && b.Height <= h);      // never grows
    }

    [Fact]
    public void Windows_that_already_fit_keep_their_size()
    {
        var b = FrameGeometry.FitWindow(1280, 820, 0, 0, 2560, 1392, 1.0);
        Assert.Equal((1280d, 820d), (b.Width, b.Height));
        Assert.Equal((640, 266), (b.X, b.Y));
    }

    [Fact]
    public void LaunchAgent_plist_escapes_arguments()
    {
        var plist = RemoteConnect.Platform.MacOS.LaunchAgentPlist.Build("com.remoteconnect.desktop", new[] { "/Applications/A & B.app/x", "--autostart" });
        Assert.Contains("A &amp; B.app", plist);
        Assert.Contains("<string>com.remoteconnect.desktop</string>", plist);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist);
    }
}
