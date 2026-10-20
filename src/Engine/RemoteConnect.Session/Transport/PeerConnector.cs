using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;

namespace RemoteConnect.Session.Transport;

public sealed class HostIdentityMismatchException(string message) : Exception(message);

/// <summary>Opens direct (TCP) or relayed (WebSocket) connections and runs the mutual-TLS handshake.</summary>
public static class PeerConnector
{
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    public static async Task<PeerConnection> ConnectDirectAsync(
        DeviceIdentity identity,
        string host,
        int port,
        string? expectedFingerprint,
        TimeSpan connectTimeout,
        CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(connectTimeout);
                await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }

            string endpoint = tcp.Client.RemoteEndPoint?.ToString() ?? $"{host}:{port}";
            return await ClientHandshakeAsync(identity, tcp.GetStream(), tcp, endpoint, ConnectionPath.Direct, expectedFingerprint, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <param name="asTlsServer">The host side of a relayed session is the TLS server, as on a direct connection.</param>
    public static async Task<PeerConnection> ConnectRelayAsync(
        DeviceIdentity identity,
        Uri relayUri,
        string token,
        string? expectedFingerprint,
        bool asTlsServer,
        CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            ws.Options.SetRequestHeader(SignalingContract.RelayTokenHeader, token);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await ws.ConnectAsync(relayUri, timeout.Token).ConfigureAwait(false);
            }

            var stream = new WebSocketStream(ws);
            string endpoint = $"relay {relayUri.Host}";
            if (asTlsServer)
            {
                var conn = await ServerHandshakeAsync(identity, stream, null, endpoint, ConnectionPath.Relay, ct).ConfigureAwait(false);
                if (expectedFingerprint is not null && !CertificateFingerprint.AreEqual(expectedFingerprint, conn.RemoteFingerprint))
                {
                    await conn.DisposeAsync().ConfigureAwait(false);
                    throw new HostIdentityMismatchException("The relayed peer is not the device the server announced.");
                }

                return conn;
            }

            return await ClientHandshakeAsync(identity, stream, null, endpoint, ConnectionPath.Relay, expectedFingerprint, ct).ConfigureAwait(false);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    public static async Task<PeerConnection> ClientHandshakeAsync(
        DeviceIdentity identity,
        Stream transport,
        IDisposable? owner,
        string endpoint,
        ConnectionPath path,
        string? expectedFingerprint,
        CancellationToken ct)
    {
        X509Certificate2? remote = null;
        var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            var options = TlsSettings.Client(identity, expectedFingerprint, c => remote = c);
            try
            {
                await ssl.AuthenticateAsClientAsync(options, timeout.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException) when (remote is not null && expectedFingerprint is not null
                && !CertificateFingerprint.AreEqual(expectedFingerprint, CertificateFingerprint.Compute(remote)))
            {
                throw new HostIdentityMismatchException(
                    "The remote device presented a different identity key than expected. " +
                    "This can mean the device was reinstalled — or that someone is impersonating it.");
            }

            if (remote is null)
            {
                throw new AuthenticationException("The remote device did not present a certificate.");
            }

            RequireStrongKey(remote);
            return new PeerConnection(ssl, owner, remote, endpoint, path);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<PeerConnection> ServerHandshakeAsync(
        DeviceIdentity identity,
        Stream transport,
        IDisposable? owner,
        string endpoint,
        ConnectionPath path,
        CancellationToken ct)
    {
        X509Certificate2? remote = null;
        var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(TlsSettings.Server(identity, c => remote = c), timeout.Token).ConfigureAwait(false);
            if (remote is null)
            {
                throw new AuthenticationException("The connecting device did not present a certificate.");
            }

            RequireStrongKey(remote);
            return new PeerConnection(ssl, owner, remote, endpoint, path);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Device keys are RSA ≥ 2048; this also makes grinding a key for a chosen 9-digit ID expensive.</summary>
    private static void RequireStrongKey(X509Certificate2 cert)
    {
        using var rsa = cert.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize < 2048)
        {
            throw new AuthenticationException("Peer device key is not an RSA key of at least 2048 bits.");
        }
    }
}
