using System.Runtime.InteropServices;
using RemoteConnect.Protocol;
using SkiaSharp;

namespace RemoteConnect.Session.Media;

/// <summary>Controller-side copy of the remote screen (BGRA, top-down). Guard access with <see cref="Sync"/>.</summary>
public sealed class RemoteFramebuffer
{
    public object Sync { get; } = new();

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; private set; } = Array.Empty<byte>();

    /// <summary>Incremented after every applied update.</summary>
    public long Version { get; private set; }

    /// <summary>Decodes every rect of <paramref name="update"/> straight into the framebuffer.</summary>
    public void Apply(FrameUpdate update)
    {
        lock (Sync)
        {
            if (update.Width != Width || update.Height != Height)
            {
                if ((update.Flags & FrameFlags.KeyFrame) == 0)
                {
                    // Size changed without a key frame: ignore partial data, the host will send a key frame.
                    return;
                }

                Width = update.Width;
                Height = update.Height;
                Pixels = new byte[Width * Height * 4];
            }

            var handle = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
            try
            {
                IntPtr basePtr = handle.AddrOfPinnedObject();
                int stride = Stride;
                if (update.Rects.Count > 2)
                {
                    Parallel.ForEach(update.Rects, r => DecodeRect(basePtr, stride, r));
                }
                else
                {
                    foreach (var r in update.Rects)
                    {
                        DecodeRect(basePtr, stride, r);
                    }
                }
            }
            finally
            {
                handle.Free();
            }

            Version++;
        }
    }

    private static void DecodeRect(IntPtr basePtr, int stride, FrameRect rect)
    {
        using var data = SKData.CreateCopy(rect.Data.Span);
        using var codec = SKCodec.Create(data) ?? throw new ProtocolException("Undecodable rect data.");
        if (codec.Info.Width != rect.Width || codec.Info.Height != rect.Height)
        {
            throw new ProtocolException("Rect image size does not match its header.");
        }

        var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        IntPtr dst = basePtr + (rect.Y * stride) + (rect.X * 4);
        var result = codec.GetPixels(info, dst, stride, SKCodecOptions.Default);
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new ProtocolException($"Rect decode failed: {result}.");
        }
    }
}
