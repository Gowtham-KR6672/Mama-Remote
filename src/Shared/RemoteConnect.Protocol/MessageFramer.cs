using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace RemoteConnect.Protocol;

public sealed class ProtocolException(string message) : Exception(message);

/// <summary>A decoded message; <see cref="Payload"/> is owned by the caller.</summary>
public readonly record struct Frame(MessageType Type, byte[] Payload, int Length)
{
    public ReadOnlySpan<byte> Span => Payload.AsSpan(0, Length);

    public ReadOnlyMemory<byte> Memory => Payload.AsMemory(0, Length);
}

/// <summary>
/// Wire format: <c>[uint32 big-endian length][uint8 type][payload]</c> where length = 1 + payload length.
/// Runs inside TLS; framing only provides message boundaries and size limits.
/// </summary>
public static class MessageFramer
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;
    private const int HeaderBytes = 5;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };

    public static byte[] SerializeJson<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    public static async ValueTask WriteAsync(Stream stream, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length + 1 > MaxMessageBytes)
        {
            throw new ProtocolException($"Message of {payload.Length} bytes exceeds the limit.");
        }

        int total = HeaderBytes + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)(payload.Length + 1));
            buffer[4] = (byte)type;
            payload.Span.CopyTo(buffer.AsSpan(HeaderBytes));
            await stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static ValueTask WriteJsonAsync<T>(Stream stream, MessageType type, T message, CancellationToken ct)
        => WriteAsync(stream, type, JsonSerializer.SerializeToUtf8Bytes(message, Json), ct);

    /// <summary>Reads one message, or returns null on a clean end-of-stream between messages.</summary>
    public static async ValueTask<Frame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderBytes];
        if (!await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length < 1 || length > MaxMessageBytes)
        {
            throw new ProtocolException($"Invalid message length {length}.");
        }

        var type = (MessageType)header[4];
        int payloadLength = (int)length - 1;
        var payload = new byte[payloadLength];
        if (payloadLength > 0 && !await ReadExactlyOrEofAsync(stream, payload, ct).ConfigureAwait(false))
        {
            throw new ProtocolException("Connection closed in the middle of a message.");
        }

        return new Frame(type, payload, payloadLength);
    }

    public static T ParseJson<T>(in Frame frame)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(frame.Span, Json)
                ?? throw new ProtocolException($"Empty {typeof(T).Name}.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Malformed {typeof(T).Name}: {ex.Message}");
        }
    }

    private static async ValueTask<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new ProtocolException("Connection closed in the middle of a message.");
            }

            read += n;
        }

        return true;
    }
}
