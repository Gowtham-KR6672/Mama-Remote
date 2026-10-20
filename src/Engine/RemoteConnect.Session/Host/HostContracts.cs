using RemoteConnect.Protocol;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.Session.Host;

public enum ApprovalDecision
{
    Reject,
    ViewOnly,
    Accept,
}

/// <summary>Host policy, re-read for every incoming request so settings changes apply immediately.</summary>
public sealed record HostOptions
{
    public string Alias { get; init; } = Environment.MachineName;

    public string Platform { get; init; } = "Unknown";

    public string AppVersion { get; init; } = "0.0.0";

    /// <summary>Master switch: when false every incoming request is refused.</summary>
    public bool AllowIncoming { get; init; } = true;

    /// <summary>
    /// False in service mode: the host may be at the sign-in screen with nobody to answer a prompt, so the opt-in
    /// unattended password is the only way in and no approval prompt is ever shown.
    /// </summary>
    public bool AttendedApprovalAvailable { get; init; } = true;

    /// <summary>Granted when the local user clicks Accept.</summary>
    public SessionPermissions AttendedPermissions { get; init; } =
        SessionPermissions.ViewScreen | SessionPermissions.ControlInput | SessionPermissions.Clipboard;

    /// <summary>Unattended access is OFF unless the user explicitly enables it and sets a password.</summary>
    public bool UnattendedEnabled { get; init; }

    public string? UnattendedPasswordHash { get; init; }

    /// <summary>Controller device IDs allowed to use the password. Empty = any device that knows the password.</summary>
    public IReadOnlyList<string> UnattendedAllowList { get; init; } = Array.Empty<string>();

    public SessionPermissions UnattendedPermissions { get; init; } =
        SessionPermissions.ViewScreen | SessionPermissions.ControlInput | SessionPermissions.Clipboard;

    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxSessions { get; init; } = 1;

    public QualityPreset DefaultQuality { get; init; } = QualityPreset.Balanced;

    public bool IsUnattendedAvailableFor(string deviceId)
        => UnattendedEnabled
           && !string.IsNullOrEmpty(UnattendedPasswordHash)
           && (UnattendedAllowList.Count == 0 || UnattendedAllowList.Contains(deviceId));
}

public sealed record IncomingRequest(
    string DeviceId,
    string Alias,
    string Platform,
    string Fingerprint,
    string RemoteEndpoint,
    ConnectionPath Path,
    SessionPermissions Requested,
    DateTimeOffset ExpiresAt,
    bool UnattendedAvailable);

/// <summary>Implemented by the UI: approval prompt and the always-visible session indicator.</summary>
public interface IHostInteraction
{
    /// <summary>Show the Accept / View-only / Reject prompt. <paramref name="ct"/> fires on timeout or when the request is withdrawn.</summary>
    Task<ApprovalDecision> RequestApprovalAsync(IncomingRequest request, CancellationToken ct);

    void OnSessionStarted(HostSession session);

    void OnSessionEnded(HostSession session, DisconnectReason reason, string? detail);
}

/// <summary>Text clipboard access (UI-thread bound in Avalonia, so it is abstracted).</summary>
public interface IClipboardBridge
{
    Task<string?> GetTextAsync();

    Task SetTextAsync(string text);
}
