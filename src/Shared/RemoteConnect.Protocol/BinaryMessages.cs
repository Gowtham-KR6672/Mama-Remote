using System.Buffers.Binary;

namespace RemoteConnect.Protocol;

/// <summary>Pointer position normalized to the active monitor: 0 = left/top edge, 65535 = right/bottom edge.</summary>
public readonly record struct PointerMessage(PointerAction Action, MouseButton Button, ushort X, ushort Y)
{
    public const int Size = 6;

    public byte[] Encode()
    {
        var b = new byte[Size];
        b[0] = (byte)Action;
        b[1] = (byte)Button;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), X);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), Y);
        return b;
    }

    public static PointerMessage Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != Size)
        {
            throw new ProtocolException("Bad pointer message.");
        }

        var action = (PointerAction)s[0];
        var button = (MouseButton)s[1];
        if (!Enum.IsDefined(action) || !Enum.IsDefined(button))
        {
            throw new ProtocolException("Bad pointer action/button.");
        }

        return new PointerMessage(action, button, BinaryPrimitives.ReadUInt16BigEndian(s[2..]), BinaryPrimitives.ReadUInt16BigEndian(s[4..]));
    }
}

/// <summary>Scroll delta in Windows wheel units (120 = one notch). Positive Y = scroll up / away from user.</summary>
public readonly record struct ScrollMessage(ushort X, ushort Y, short DeltaX, short DeltaY)
{
    public const int Size = 8;

    public byte[] Encode()
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt16BigEndian(b, X);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), Y);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(4), DeltaX);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6), DeltaY);
        return b;
    }

    public static ScrollMessage Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != Size)
        {
            throw new ProtocolException("Bad scroll message.");
        }

        return new ScrollMessage(
            BinaryPrimitives.ReadUInt16BigEndian(s),
            BinaryPrimitives.ReadUInt16BigEndian(s[2..]),
            BinaryPrimitives.ReadInt16BigEndian(s[4..]),
            BinaryPrimitives.ReadInt16BigEndian(s[6..]));
    }
}

/// <summary>Physical key as a USB HID usage (page 0x07), so it maps to both Windows scan codes and macOS key codes.</summary>
public readonly record struct KeyMessage(ushort HidUsage, bool Down)
{
    public const int Size = 3;

    public byte[] Encode()
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt16BigEndian(b, HidUsage);
        b[2] = Down ? (byte)1 : (byte)0;
        return b;
    }

    public static KeyMessage Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length != Size || s[2] > 1)
        {
            throw new ProtocolException("Bad key message.");
        }

        return new KeyMessage(BinaryPrimitives.ReadUInt16BigEndian(s), s[2] == 1);
    }
}

public static class SimpleMessages
{
    public static byte[] EncodeUInt32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    public static uint DecodeUInt32(ReadOnlySpan<byte> s)
        => s.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(s) : throw new ProtocolException("Bad uint32 message.");

    public static byte[] EncodeInt64(long value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        return b;
    }

    public static long DecodeInt64(ReadOnlySpan<byte> s)
        => s.Length == 8 ? BinaryPrimitives.ReadInt64BigEndian(s) : throw new ProtocolException("Bad int64 message.");
}
