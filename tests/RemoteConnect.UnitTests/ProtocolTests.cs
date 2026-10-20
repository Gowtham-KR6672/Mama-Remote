using System.Buffers.Binary;
using RemoteConnect.Protocol;

namespace RemoteConnect.UnitTests;

public class ProtocolTests
{
    [Fact]
    public async Task Framer_round_trips_messages_and_reports_clean_eof()
    {
        var ms = new MemoryStream();
        await MessageFramer.WriteAsync(ms, MessageType.Key, new KeyMessage(0x04, true).Encode(), default);
        await MessageFramer.WriteJsonAsync(ms, MessageType.Hello, new HelloMessage(1, "482913207", "Alice", "Windows", "1.0", SessionPermissions.ViewScreen), default);
        ms.Position = 0;

        var first = await MessageFramer.ReadAsync(ms, default);
        Assert.Equal(MessageType.Key, first!.Value.Type);
        Assert.Equal(new KeyMessage(0x04, true), KeyMessage.Decode(first.Value.Span));

        var second = await MessageFramer.ReadAsync(ms, default);
        var hello = MessageFramer.ParseJson<HelloMessage>(second!.Value);
        Assert.Equal("Alice", hello.Alias);
        Assert.Equal(SessionPermissions.ViewScreen, hello.RequestedPermissions);

        Assert.Null(await MessageFramer.ReadAsync(ms, default));
    }

    [Fact]
    public async Task Framer_rejects_oversized_length()
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header, MessageFramer.MaxMessageBytes + 1);
        await Assert.ThrowsAsync<ProtocolException>(async () => await MessageFramer.ReadAsync(new MemoryStream(header), default));
    }

    [Fact]
    public async Task Framer_rejects_truncated_message()
    {
        var ms = new MemoryStream();
        await MessageFramer.WriteAsync(ms, MessageType.Pointer, new byte[6], default);
        var truncated = new MemoryStream(ms.ToArray()[..^2]);
        await Assert.ThrowsAsync<ProtocolException>(async () => await MessageFramer.ReadAsync(truncated, default));
    }

    [Fact]
    public void Malformed_json_is_a_protocol_error()
    {
        var frame = new Frame(MessageType.Hello, "{not json"u8.ToArray(), 9);
        Assert.Throws<ProtocolException>(() => MessageFramer.ParseJson<HelloMessage>(frame));
    }

    [Fact]
    public void Binary_input_messages_round_trip()
    {
        var p = new PointerMessage(PointerAction.Down, MouseButton.Right, 65535, 12);
        Assert.Equal(p, PointerMessage.Decode(p.Encode()));
        var s = new ScrollMessage(100, 200, -120, 240);
        Assert.Equal(s, ScrollMessage.Decode(s.Encode()));
        Assert.Throws<ProtocolException>(() => PointerMessage.Decode(new byte[] { 9, 1, 0, 0, 0, 0 }));
        Assert.Throws<ProtocolException>(() => KeyMessage.Decode(new byte[] { 0, 4, 2 }));
    }

    [Fact]
    public void FrameUpdate_round_trips()
    {
        var update = new FrameUpdate
        {
            Sequence = 7,
            Width = 100,
            Height = 50,
            Flags = FrameFlags.KeyFrame,
            Rects = new[]
            {
                new FrameRect(0, 0, 64, 50, RectEncoding.Jpeg, new byte[] { 1, 2, 3 }),
                new FrameRect(64, 0, 36, 50, RectEncoding.Jpeg, new byte[] { 4 }),
            },
        };
        var decoded = FrameUpdate.Decode(update.Encode());
        Assert.Equal(7u, decoded.Sequence);
        Assert.Equal(2, decoded.Rects.Count);
        Assert.Equal(new byte[] { 4 }, decoded.Rects[1].Data.ToArray());
    }

    [Fact]
    public void FrameUpdate_rejects_rect_outside_frame_and_trailing_bytes()
    {
        var bad = new FrameUpdate
        {
            Sequence = 1,
            Width = 10,
            Height = 10,
            Rects = new[] { new FrameRect(5, 5, 10, 10, RectEncoding.Jpeg, new byte[] { 1 }) },
        };
        Assert.Throws<ProtocolException>(() => FrameUpdate.Decode(bad.Encode()));

        var ok = new FrameUpdate { Sequence = 1, Width = 10, Height = 10, Rects = Array.Empty<FrameRect>() }.Encode();
        Assert.Throws<ProtocolException>(() => FrameUpdate.Decode(ok.Concat(new byte[] { 0 }).ToArray()));
    }
}
