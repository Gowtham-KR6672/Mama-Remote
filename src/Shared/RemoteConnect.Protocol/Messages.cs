using System.Text.Json.Serialization;

namespace RemoteConnect.Protocol;

public static class ProtocolInfo
{
    /// <summary>Bumped on any incompatible wire change; peers with different versions refuse the session.</summary>
    public const int Version = 1;
}

public enum MessageType : byte
{
    Hello = 1,
    HelloResult = 2,
    PasswordAuth = 3,
    AuthStatus = 4,

    ScreenLayout = 10,
    SelectMonitor = 11,
    SetQuality = 12,

    FrameUpdate = 20,
    FrameAck = 21,

    Pointer = 30,
    Key = 31,
    Scroll = 32,

    ClipboardText = 40,

    PermissionsChanged = 50,

    Ping = 60,
    Pong = 61,

    Disconnect = 70,
}

[Flags]
[JsonConverter(typeof(JsonNumberEnumConverter<SessionPermissions>))]
public enum SessionPermissions
{
    None = 0,
    ViewScreen = 1,
    ControlInput = 2,
    Clipboard = 4,
    FileTransfer = 8,
}

public enum HelloOutcome
{
    Accepted = 0,
    PendingApproval = 1,
    Rejected = 2,
    Busy = 3,
    TimedOut = 4,
    LockedOut = 5,
    ProtocolMismatch = 6,
    IdentityMismatch = 7,
    NotAllowed = 8,
}

public enum QualityPreset
{
    Speed = 0,
    Balanced = 1,
    Quality = 2,
}

public enum DisconnectReason
{
    Normal = 0,
    HostDisconnected = 1,
    ControllerDisconnected = 2,
    Revoked = 3,
    Timeout = 4,
    ProtocolError = 5,
    HostShuttingDown = 6,
}

public enum PointerAction : byte
{
    Move = 0,
    Down = 1,
    Up = 2,
}

public enum MouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
}

/// <summary>Controller → host, first message after TLS.</summary>
public sealed record HelloMessage(
    int ProtocolVersion,
    string DeviceId,
    string Alias,
    string Platform,
    string AppVersion,
    SessionPermissions RequestedPermissions);

/// <summary>
/// Host → controller. May be sent twice: PendingApproval, then the final outcome. <paramref name="PasswordOnly"/>
/// means nobody can accept a prompt on the host (service mode, e.g. at the sign-in screen): only the unattended
/// password can grant access. Older peers omit it (false).
/// </summary>
public sealed record HelloResultMessage(
    HelloOutcome Outcome,
    SessionPermissions Granted,
    string HostAlias,
    string HostPlatform,
    string? Message,
    bool UnattendedAvailable,
    bool PasswordOnly = false);

/// <summary>Controller → host while approval is pending: unattended-access password.</summary>
public sealed record PasswordAuthMessage(string Password);

public sealed record AuthStatusMessage(bool Success, string Message, int AttemptsRemaining, bool LockedOut);

public sealed record RemoteMonitor(int Index, string Name, bool IsPrimary, int Width, int Height);

public sealed record ScreenLayoutMessage(IReadOnlyList<RemoteMonitor> Monitors, int ActiveMonitor);

public sealed record SelectMonitorMessage(int Index);

public sealed record SetQualityMessage(QualityPreset Preset);

public sealed record PermissionsChangedMessage(SessionPermissions Granted);

public sealed record ClipboardTextMessage(string Text)
{
    public const int MaxChars = 512 * 1024;
}

public sealed record DisconnectMessage(DisconnectReason Reason, string? Message);
