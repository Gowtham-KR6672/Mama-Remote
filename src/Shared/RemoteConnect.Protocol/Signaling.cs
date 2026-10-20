namespace RemoteConnect.Protocol.Signaling;

/// <summary>Names shared by the SignalR hub (server) and the client.</summary>
public static class SignalingContract
{
    public const string HubPath = "/hubs/signal";
    public const string RelayPath = "/relay";
    public const string RelayTokenHeader = "X-RemoteConnect-Relay-Token";

    // Client → server
    public const string GetChallenge = nameof(GetChallenge);
    public const string Register = nameof(Register);
    public const string UpdateEndpoints = nameof(UpdateEndpoints);
    public const string Resolve = nameof(Resolve);
    public const string RequestRelay = nameof(RequestRelay);

    // Server → client
    public const string RelayOffer = nameof(RelayOffer);
    public const string SessionReplaced = nameof(SessionReplaced);
}

public enum RegisterError
{
    None = 0,
    InvalidRequest = 1,
    BadSignature = 2,
    IdMismatch = 3,
    IdConflict = 4,
    RegistrationKeyInvalid = 5,
    ChallengeExpired = 6,
}

public sealed record RegisterRequest(
    string DeviceId,
    string CertificateBase64,
    string Challenge,
    string SignatureBase64,
    string Alias,
    string Platform,
    string AppVersion,
    string? RegistrationKey,
    string[] LanEndpoints);

public sealed record RegisterResponse(bool Success, RegisterError Error, string? Message, DateTimeOffset ServerTime);

public sealed record ResolveResponse(
    bool Found,
    bool Online,
    string? DeviceId,
    string? Fingerprint,
    string? Alias,
    string? Platform,
    string[] LanEndpoints,
    string? Error);

public sealed record RelayTicket(bool Success, string? SessionId, string? Token, string? HostFingerprint, string? Error);

/// <summary>Sent to the host when a controller asks the server to relay a session.</summary>
public sealed record RelayOffer(
    string SessionId,
    string Token,
    string ControllerId,
    string ControllerFingerprint,
    string ControllerAlias);

/// <summary>UDP broadcast on the local network so devices can be found without a server.</summary>
public sealed record LanAnnouncement(int V, string DeviceId, string Alias, string Platform, int Port, string Fingerprint)
{
    public const int DefaultPort = 47071;
    public const int DefaultTcpPort = 47070;
}
