using System.Runtime.InteropServices;
using RemoteConnect.Platform;
using RemoteConnect.Protocol;
using SkiaSharp;

namespace RemoteConnect.Session.Media;

/// <summary>
/// Dirty-tile screen encoder: compares the new frame with the previous one in 64x64 tiles, merges changed tiles
/// into rectangles and JPEG-encodes only those (VNC "tight"-style). Static screens cost almost nothing.
/// This is the M1 codec; hardware H.264 is the planned M2 codec behind the same FrameUpdate envelope.
/// </summary>
public sealed class TileEncoder
{
    public const int TileSize = 64;

    /// <summary>When more than this fraction of the screen changed, send one full rect instead of many.</summary>
    private const double FullFrameThreshold = 0.55;

    private uint _sequence;

    public int Quality { get; set; } = 65;

    public FrameUpdate? Encode(FrameBuffer current, FrameBuffer? previous, bool forceKeyFrame)
    {
        bool keyFrame = forceKeyFrame || previous is null
            || previous.Width != current.Width || previous.Height != current.Height;

        List<(int X, int Y, int W, int H)> rects;
        if (keyFrame)
        {
            rects = new() { (0, 0, current.Width, current.Height) };
        }
        else
        {
            rects = DiffRects(current, previous!);
            if (rects.Count == 0)
            {
                return null;
            }

            long dirty = rects.Sum(r => (long)r.W * r.H);
            if (dirty > FullFrameThreshold * current.Width * current.Height)
            {
                rects = new() { (0, 0, current.Width, current.Height) };
            }
        }

        var encoded = new FrameRect[rects.Count];
        var handle = GCHandle.Alloc(current.Pixels, GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = handle.AddrOfPinnedObject();
            int stride = current.Stride;
            int quality = Math.Clamp(Quality, 10, 95);
            if (rects.Count > 2)
            {
                Parallel.For(0, rects.Count, i => encoded[i] = EncodeRect(basePtr, stride, rects[i], quality));
            }
            else
            {
                for (int i = 0; i < rects.Count; i++)
                {
                    encoded[i] = EncodeRect(basePtr, stride, rects[i], quality);
                }
            }
        }
        finally
        {
            handle.Free();
        }

        return new FrameUpdate
        {
            Sequence = ++_sequence,
            Width = checked((ushort)current.Width),
            Height = checked((ushort)current.Height),
            Flags = keyFrame ? FrameFlags.KeyFrame : FrameFlags.None,
            Rects = encoded,
        };
    }

    private static FrameRect EncodeRect(IntPtr basePtr, int stride, (int X, int Y, int W, int H) r, int quality)
    {
        var info = new SKImageInfo(r.W, r.H, SKColorType.Bgra8888, SKAlphaType.Opaque);
        IntPtr origin = basePtr + (r.Y * stride) + (r.X * 4);
        using var pixmap = new SKPixmap(info, origin, stride);
        using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
            ?? throw new InvalidOperationException("JPEG encoding failed.");
        return new FrameRect((ushort)r.X, (ushort)r.Y, (ushort)r.W, (ushort)r.H, RectEncoding.Jpeg, data.ToArray());
    }

    /// <summary>Changed tiles merged into horizontal runs, then runs with identical spans merged vertically.</summary>
    internal static List<(int X, int Y, int W, int H)> DiffRects(FrameBuffer current, FrameBuffer previous)
    {
        int cols = (current.Width + TileSize - 1) / TileSize;
        int rows = (current.Height + TileSize - 1) / TileSize;
        int stride = current.Stride;
        var cur = current.Pixels;
        var prev = previous.Pixels;

        var dirty = new bool[rows, cols];
        Parallel.For(0, rows, ty =>
        {
            int y0 = ty * TileSize;
            int y1 = Math.Min(current.Height, y0 + TileSize);
            for (int tx = 0; tx < cols; tx++)
            {
                int x0 = tx * TileSize;
                int bytes = (Math.Min(current.Width, x0 + TileSize) - x0) * 4;
                for (int y = y0; y < y1; y++)
                {
                    int o = (y * stride) + (x0 * 4);
                    if (!cur.AsSpan(o, bytes).SequenceEqual(prev.AsSpan(o, bytes)))
                    {
                        dirty[ty, tx] = true;
                        break;
                    }
                }
            }
        });

        // Horizontal runs per tile row.
        var open = new List<(int X, int Y, int W, int H)>();
        var result = new List<(int X, int Y, int W, int H)>();
        for (int ty = 0; ty < rows; ty++)
        {
            int y0 = ty * TileSize;
            int h = Math.Min(current.Height, y0 + TileSize) - y0;
            var rowRuns = new List<(int X, int Y, int W, int H)>();
            int tx = 0;
            while (tx < cols)
            {
                if (!dirty[ty, tx])
                {
                    tx++;
                    continue;
                }

                int start = tx;
                while (tx < cols && dirty[ty, tx])
                {
                    tx++;
                }

                int x0 = start * TileSize;
                int x1 = Math.Min(current.Width, tx * TileSize);
                rowRuns.Add((x0, y0, x1 - x0, h));
            }

            // Extend rects from the previous row that have exactly the same horizontal span.
            var nextOpen = new List<(int X, int Y, int W, int H)>();
            foreach (var run in rowRuns)
            {
                int idx = open.FindIndex(o => o.X == run.X && o.W == run.W && o.Y + o.H == run.Y);
                if (idx >= 0)
                {
                    var o = open[idx];
                    open.RemoveAt(idx);
                    nextOpen.Add((o.X, o.Y, o.W, o.H + run.H));
                }
                else
                {
                    nextOpen.Add(run);
                }
            }

            result.AddRange(open); // rects that did not continue are finished
            open = nextOpen;
        }

        result.AddRange(open);
        return result;
    }
}
