using System.Text.Json.Serialization;

namespace RemoteConnect.LocalIpc;

/// <summary>
/// Every message the service and the session agent can exchange. There is deliberately no message that carries a
/// command, a path or a program to run: the pipe only reports state.
/// </summary>
public enum IpcMessageType : byte
{
    /// <summary>Liveness, both directions. The service echoes the agent's sequence number.</summary>
    Heartbeat = 1,

    /// <summary>Agent → service only: what the agent is doing (for the public status and the supervisor).</summary>
    Status = 2,
}

/// <summary>Which end of the pipe sent a message. Decoding enforces the direction each type may travel.</summary>
public enum IpcSender
{
    Service,
    Agent,
}

public enum AgentState
{
    Starting = 0,
    Ready = 1,

    /// <summary>Running, but something is wrong (e.g. the LAN port is taken); the details are in the agent log.</summary>
    Degraded = 2,
    Stopping = 3,
}

/// <summary>Mirror of the signaling client's status, without depending on the session engine.</summary>
public enum AgentServerStatus
{
    Disabled = 0,
    Connecting = 1,
    Online = 2,
    Offline = 3,
    Rejected = 4,
}

public abstract record IpcMessage
{
    [JsonIgnore]
    public abstract IpcMessageType Type { get; }
}

public sealed record HeartbeatMessage([property: JsonRequired] long Sequence) : IpcMessage
{
    [JsonIgnore]
    public override IpcMessageType Type => IpcMessageType.Heartbeat;
}

public sealed record AgentStatusMessage(
    [property: JsonRequired] AgentState State,
    [property: JsonRequired] string DeviceId,
    [property: JsonRequired] string Fingerprint,
    [property: JsonRequired] string InputDesktop,
    [property: JsonRequired] int? LanPort,
    [property: JsonRequired] AgentServerStatus Server,
    [property: JsonRequired] int ActiveSessions,
    [property: JsonRequired] bool UnattendedReady,
    [property: JsonRequired] string AgentVersion) : IpcMessage
{
    [JsonIgnore]
    public override IpcMessageType Type => IpcMessageType.Status;
}
