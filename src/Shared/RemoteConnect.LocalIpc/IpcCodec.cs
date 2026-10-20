using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteConnect.Security;

namespace RemoteConnect.LocalIpc;

public sealed class IpcProtocolException(string message) : Exception(message);

/// <summary>
/// Wire format of the service ↔ agent pipe: <c>[u32 little-endian payload length][u8 type][UTF-8 JSON]</c>.
/// Decoding is strict — bounded size, known type, allowed direction, no unknown or missing fields, and every value
/// range-checked — so a misbehaving peer can only get its connection dropped.
/// </summary>
public static class IpcCodec
{
    public const int HeaderSize = 5;
    public const int MaxPayloadBytes = 2048;
    public const int MaxActiveSessions = 64;
    public const int MaxTextLength = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Validates <paramref name="message"/> as if the other side were receiving it, then frames it.</summary>
    public static byte[] Encode(IpcMessage message, IpcSender sender)
    {
        Validate(message, sender);
        byte[] payload = message switch
        {
            HeartbeatMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Json),
            AgentStatusMessage m => JsonSerializer.SerializeToUtf8Bytes(m, Json),
            _ => throw new IpcProtocolException($"Unsupported message {message.GetType().Name}."),
        };
        if (payload.Length > MaxPayloadBytes)
        {
            throw new IpcProtocolException("Message too large.");
        }

        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        frame[4] = (byte)message.Type;
        payload.CopyTo(frame, HeaderSize);
        return frame;
    }

    /// <summary>Checks a frame header and returns the payload length that follows it.</summary>
    public static int ReadHeader(ReadOnlySpan<byte> header, out IpcMessageType type)
    {
        if (header.Length != HeaderSize)
        {
            throw new IpcProtocolException("Truncated header.");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxPayloadBytes)
        {
            throw new IpcProtocolException($"Invalid payload length {length}.");
        }

        type = (IpcMessageType)header[4];
        if (!Enum.IsDefined(type))
        {
            throw new IpcProtocolException($"Unknown message type {header[4]}.");
        }

        return (int)length;
    }

    public static IpcMessage Decode(IpcMessageType type, ReadOnlySpan<byte> payload, IpcSender sender)
    {
        if (payload.Length is 0 or > MaxPayloadBytes)
        {
            throw new IpcProtocolException("Invalid payload length.");
        }

        IpcMessage? message;
        try
        {
            message = type switch
            {
                IpcMessageType.Heartbeat => JsonSerializer.Deserialize<HeartbeatMessage>(payload, Json),
                IpcMessageType.Status => JsonSerializer.Deserialize<AgentStatusMessage>(payload, Json),
                _ => throw new IpcProtocolException($"Unknown message type {(byte)type}."),
            };
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException("Malformed message: " + ex.Message);
        }

        if (message is null)
        {
            throw new IpcProtocolException("Empty message.");
        }

        Validate(message, sender);
        return message;
    }

    public static void Validate(IpcMessage message, IpcSender sender)
    {
        switch (message)
        {
            case HeartbeatMessage m:
                Require(m.Sequence >= 0, "Heartbeat sequence must not be negative.");
                break;

            case AgentStatusMessage m:
                Require(sender == IpcSender.Agent, "Only the agent sends status.");
                Require(Enum.IsDefined(m.State), "Unknown agent state.");
                Require(Enum.IsDefined(m.Server), "Unknown server status.");
                Require(DeviceIds.TryNormalize(m.DeviceId, out var id) && id == m.DeviceId, "Invalid device ID.");
                Require(IsFingerprint(m.Fingerprint), "Invalid fingerprint.");
                Require(DeviceIds.TryFromFingerprint(m.Fingerprint, out var derived) && derived == m.DeviceId, "Device ID does not match the fingerprint.");
                Require(IsName(m.InputDesktop, allowEmpty: true), "Invalid desktop name.");
                Require(m.LanPort is null or (> 0 and <= 65535), "Invalid port.");
                Require(m.ActiveSessions is >= 0 and <= MaxActiveSessions, "Invalid session count.");
                Require(IsName(m.AgentVersion, allowEmpty: false), "Invalid version.");
                break;

            default:
                throw new IpcProtocolException($"Unsupported message {message.GetType().Name}.");
        }
    }

    private static void Require(bool condition, string error)
    {
        if (!condition)
        {
            throw new IpcProtocolException(error);
        }
    }

    private static bool IsFingerprint(string? value)
        => value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    /// <summary>Short identifiers only (desktop names such as "Winlogon", versions such as "0.2.0").</summary>
    private static bool IsName(string? value, bool allowEmpty)
        => value is not null
           && (allowEmpty || value.Length > 0)
           && value.Length <= MaxTextLength
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '+');
}
