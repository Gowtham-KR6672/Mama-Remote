using System.Buffers.Binary;

namespace RemoteConnect.Protocol;

public enum RectEncoding : byte
{
    Jpeg = 1,
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    KeyFrame = 1,
}

public readonly record struct FrameRect(ushort X, ushort Y, ushort Width, ushort Height, RectEncoding Encoding, ReadOnlyMemory<byte> Data);

/// <summary>
/// One screen update: a set of independently-encoded rectangles over a frame of <see cref="Width"/> x <see cref="Height"/>.
/// Layout: [u32 seq][u16 width][u16 height][u8 flags][u16 count] then per rect [u16 x][u16 y][u16 w][u16 h][u8 enc][u32 len][data].
/// </summary>
public sealed class FrameUpdate
{
    public const int MaxRects = 4096;
    private const int HeaderSize = 11;
    private const int RectHeaderSize = 13;

    public required uint Sequence { get; init; }

    public required ushort Width { get; init; }

    public required ushort Height { get; init; }

    public FrameFlags Flags { get; init; }

    public required IReadOnlyList<FrameRect> Rects { get; init; }

    public byte[] Encode()
    {
        int size = HeaderSize;
        foreach (var r in Rects)
        {
            size += RectHeaderSize + r.Data.Length;
        }

        var buffer = new byte[size];
        var s = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], Width);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], Height);
        s[8] = (byte)Flags;
        BinaryPrimitives.WriteUInt16BigEndian(s[9..], (ushort)Rects.Count);
        int o = HeaderSize;
        foreach (var r in Rects)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[o..], r.X);
            BinaryPrimitives.WriteUInt16BigEndian(s[(o + 2)..], r.Y);
            BinaryPrimitives.WriteUInt16BigEndian(s[(o + 4)..], r.Width);
            BinaryPrimitives.WriteUInt16BigEndian(s[(o + 6)..], r.Height);
            s[o + 8] = (byte)r.Encoding;
            BinaryPrimitives.WriteUInt32BigEndian(s[(o + 9)..], (uint)r.Data.Length);
            o += RectHeaderSize;
            r.Data.Span.CopyTo(s[o..]);
            o += r.Data.Length;
        }

        return buffer;
    }

    /// <summary>Parses and bounds-checks an update. Rect data slices reference <paramref name="payload"/> (no copy).</summary>
    public static FrameUpdate Decode(ReadOnlyMemory<byte> payload)
    {
        var s = payload.Span;
        if (s.Length < HeaderSize)
        {
            throw new ProtocolException("Frame update too short.");
        }

        uint seq = BinaryPrimitives.ReadUInt32BigEndian(s);
        ushort width = BinaryPrimitives.ReadUInt16BigEndian(s[4..]);
        ushort height = BinaryPrimitives.ReadUInt16BigEndian(s[6..]);
        var flags = (FrameFlags)s[8];
        int count = BinaryPrimitives.ReadUInt16BigEndian(s[9..]);
        if (width == 0 || height == 0 || count > MaxRects)
        {
            throw new ProtocolException("Invalid frame header.");
        }

        var rects = new List<FrameRect>(count);
        int o = HeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (o + RectHeaderSize > s.Length)
            {
                throw new ProtocolException("Truncated rect header.");
            }

            ushort x = BinaryPrimitives.ReadUInt16BigEndian(s[o..]);
            ushort y = BinaryPrimitives.ReadUInt16BigEndian(s[(o + 2)..]);
            ushort w = BinaryPrimitives.ReadUInt16BigEndian(s[(o + 4)..]);
            ushort h = BinaryPrimitives.ReadUInt16BigEndian(s[(o + 6)..]);
            var enc = (RectEncoding)s[o + 8];
            uint len = BinaryPrimitives.ReadUInt32BigEndian(s[(o + 9)..]);
            o += RectHeaderSize;
            if (w == 0 || h == 0 || x + w > width || y + h > height)
            {
                throw new ProtocolException("Rect outside frame bounds.");
            }

            if (enc != RectEncoding.Jpeg)
            {
                throw new ProtocolException($"Unsupported rect encoding {(byte)enc}.");
            }

            if (len > (uint)(s.Length - o))
            {
                throw new ProtocolException("Truncated rect data.");
            }

            rects.Add(new FrameRect(x, y, w, h, enc, payload.Slice(o, (int)len)));
            o += (int)len;
        }

        if (o != s.Length)
        {
            throw new ProtocolException("Trailing bytes after frame update.");
        }

        return new FrameUpdate { Sequence = seq, Width = width, Height = height, Flags = flags, Rects = rects };
    }
}
