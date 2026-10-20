using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RemoteConnect.LocalIpc.Windows;

public sealed class IpcPeerRejectedException(string message) : Exception(message);

/// <summary>
/// The local pipe between the service (server) and its session agent (client). Three independent checks keep other
/// processes out: the pipe ACL admits only LocalSystem (and denies network logons), the server verifies that the
/// client is exactly the agent process it launched, and the client verifies that the server is the service binary
/// running as LocalSystem — while never letting the server impersonate it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServicePipe
{
    public static PipeSecurity CreateSecurity() => CreateSecurity(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));

    /// <summary>Tests use the current user instead of LocalSystem; production always uses <see cref="CreateSecurity()"/>.</summary>
    internal static PipeSecurity CreateSecurity(SecurityIdentifier allowed)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(allowed, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Agent side. Connects with <see cref="TokenImpersonationLevel.Identification"/>: even if another process had
    /// squatted on the pipe name, it could not impersonate the (LocalSystem) agent. The server's identity is checked
    /// before a single byte is sent.
    /// </summary>
    public static async Task<IpcChannel> ConnectAsync(
        string pipeName, string expectedServerImage, bool requireLocalSystem, TimeSpan timeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
            var peer = PipePeer.Server(pipe.SafePipeHandle);
            if (requireLocalSystem && !peer.IsLocalSystem)
            {
                throw new IpcPeerRejectedException($"The pipe server (process {peer.ProcessId}) is not running as LocalSystem.");
            }

            if (!ServiceModeLayout.SamePath(peer.ImagePath, expectedServerImage))
            {
                throw new IpcPeerRejectedException($"The pipe server '{peer.ImagePath}' is not the RemoteConnect service.");
            }

            return new IpcChannel(pipe, IpcSender.Agent, peer.ProcessId);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Service side: one pipe instance per launched agent.</summary>
[SupportedOSPlatform("windows")]
public sealed class AgentPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;

    private AgentPipeServer(NamedPipeServerStream pipe) => _pipe = pipe;

    /// <summary>
    /// Creates the pipe as its first and only instance. If any other process already holds the name (pipe squatting),
    /// this throws instead of sharing the name with it.
    /// </summary>
    public static AgentPipeServer Create(string pipeName, PipeSecurity security)
    {
        int buffer = IpcCodec.HeaderSize + IpcCodec.MaxPayloadBytes;
        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            buffer,
            buffer,
            security);
        return new AgentPipeServer(pipe);
    }

    /// <summary>Waits for the agent and checks that the client is exactly the process the service launched.</summary>
    public async Task<IpcChannel> AcceptAsync(int expectedProcessId, string expectedImagePath, CancellationToken ct)
    {
        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        var peer = PipePeer.Client(_pipe.SafePipeHandle);
        if (peer.ProcessId != expectedProcessId)
        {
            throw new IpcPeerRejectedException($"Pipe client is process {peer.ProcessId}, not the agent the service started ({expectedProcessId}).");
        }

        if (!ServiceModeLayout.SamePath(peer.ImagePath, expectedImagePath))
        {
            throw new IpcPeerRejectedException($"Pipe client '{peer.ImagePath}' is not the RemoteConnect agent.");
        }

        return new IpcChannel(_pipe, IpcSender.Service, peer.ProcessId);
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
