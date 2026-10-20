using RemoteConnect.Platform;
using RemoteConnect.Session.Media;

namespace RemoteConnect.UnitTests;

public class MediaTests
{
    private static FrameBuffer Solid(int w, int h, byte r, byte g, byte b)
    {
        var f = new FrameBuffer();
        f.EnsureSize(w, h);
        for (int i = 0; i < f.Pixels.Length; i += 4)
        {
            f.Pixels[i] = b;
            f.Pixels[i + 1] = g;
            f.Pixels[i + 2] = r;
            f.Pixels[i + 3] = 255;
        }

        return f;
    }

    [Fact]
    public void Unchanged_frame_produces_no_update()
    {
        var enc = new TileEncoder();
        var a = Solid(320, 200, 10, 20, 30);
        var b = Solid(320, 200, 10, 20, 30);
        Assert.Null(enc.Encode(b, a, forceKeyFrame: false));
    }

    [Fact]
    public void Single_pixel_change_produces_one_tile_rect()
    {
        var prev = Solid(320, 200, 10, 20, 30);
        var cur = Solid(320, 200, 10, 20, 30);
        int o = (130 * cur.Stride) + (70 * 4);
        cur.Pixels[o] = 255;
        var rects = TileEncoder.DiffRects(cur, prev);
        var r = Assert.Single(rects);
        Assert.Equal((64, 128, 64, 64), r);
    }

    [Fact]
    public void Edge_tiles_are_clipped_to_frame_size()
    {
        var prev = Solid(100, 70, 0, 0, 0);
        var cur = Solid(100, 70, 0, 0, 0);
        cur.Pixels[(69 * cur.Stride) + (99 * 4)] = 1;
        var r = Assert.Single(TileEncoder.DiffRects(cur, prev));
        Assert.Equal((64, 64, 36, 6), r);
    }

    [Fact]
    public void Vertically_adjacent_identical_runs_merge()
    {
        var prev = Solid(256, 256, 0, 0, 0);
        var cur = Solid(256, 256, 0, 0, 0);
        foreach (var y in new[] { 10, 80, 150 })
        {
            cur.Pixels[(y * cur.Stride) + (70 * 4)] = 9;
        }

        var r = Assert.Single(TileEncoder.DiffRects(cur, prev));
        Assert.Equal((64, 0, 64, 192), r);
    }

    [Fact]
    public void Encoded_frame_decodes_to_approximately_the_same_image()
    {
        var src = Solid(256, 128, 200, 40, 90);
        var enc = new TileEncoder { Quality = 90 };
        var update = enc.Encode(src, null, forceKeyFrame: true);
        Assert.NotNull(update);
        Assert.True(update!.Flags.HasFlag(RemoteConnect.Protocol.FrameFlags.KeyFrame));

        var fb = new RemoteFramebuffer();
        fb.Apply(RemoteConnect.Protocol.FrameUpdate.Decode(update.Encode()));
        Assert.Equal(256, fb.Width);
        Assert.Equal(128, fb.Height);
        int mid = (64 * fb.Stride) + (128 * 4);
        Assert.InRange(fb.Pixels[mid + 2], 190, 210); // R
        Assert.InRange(fb.Pixels[mid + 1], 30, 50);   // G
        Assert.InRange(fb.Pixels[mid], 80, 100);      // B
    }

    [Fact]
    public void Size_change_forces_key_frame()
    {
        var enc = new TileEncoder();
        var update = enc.Encode(Solid(200, 100, 1, 2, 3), Solid(100, 100, 1, 2, 3), forceKeyFrame: false);
        Assert.NotNull(update);
        Assert.Single(update!.Rects);
        Assert.Equal(200, update.Rects[0].Width);
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080, 1920, 1080)]
    [InlineData(1280, 720, 1920, 1080, 1280, 720)]
    [InlineData(2560, 1600, 1920, 1080, 1728, 1080)]
    [InlineData(1001, 501, 4000, 4000, 1000, 500)]
    public void Fit_preserves_aspect_never_upscales_and_keeps_even_sizes(int w, int h, int mw, int mh, int ew, int eh)
        => Assert.Equal((ew, eh), FrameGeometry.Fit(w, h, mw, mh));
}
