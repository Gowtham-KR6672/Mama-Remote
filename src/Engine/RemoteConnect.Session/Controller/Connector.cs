using Microsoft.Extensions.Logging;
using RemoteConnect.Security;
using RemoteConnect.Session.Discovery;
using RemoteConnect.Session.Signaling;
using RemoteConnect.Session.Transport;

namespace RemoteConnect.Session.Controller;

/// <summary>Remembers which identity key belongs to which device/address (like SSH known_hosts).</summary>
public interface IKnownHostStore
{
    string? GetFingerprint(string key);

    void Remember(string key, string fingerprint);
}

/// <summary>Shown when connecting by IP to a device never seen before (trust on first use).</summary>
public sealed record UnknownHostInfo(string Target, string DeviceId, string Fingerprint);

public sealed class ConnectException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Turns what the user typed into an authenticated connection:
/// IP/host → direct; ID → LAN discovery, then server-published LAN endpoints, then the server relay.
/// The host's key fingerprint is always pinned (known-hosts, server, or self-consistent LAN announcement).
/// </summary>
public sealed class Connector
{
    private static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(2);

    private readonly DeviceIdentity _identity;
    private readonly SignalingClient? _signaling;
    private readonly LanDiscovery? _discovery;
    private readonly IKnownHostStore _knownHosts;
    private readonly ILogger _logger;

    public Connector(DeviceIdentity identity, SignalingClient? signaling, LanDiscovery? discovery, IKnownHostStore knownHosts, ILogger logger)
    {
        _identity = identity;
        _signaling = signaling;
        _discovery = discovery;
        _knownHosts = knownHosts;
        _logger = logger;
    }

    public int DefaultPort { get; set; } = RemoteConnect.Protocol.Signaling.LanAnnouncement.DefaultTcpPort;

    public async Task<PeerConnection> ConnectAsync(
        ConnectTarget target,
        Func<UnknownHostInfo, Task<bool>> confirmUnknownHost,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!target.IsDeviceId)
        {
            return await ConnectByAddressAsync(target, confirmUnknownHost, progress, ct).ConfigureAwait(false);
        }

        string id = target.DeviceId!;
        if (id == _identity.DeviceId)
        {
            throw new ConnectException("That is this device's own ID.");
        }

        string? pinned = _knownHosts.GetFingerprint(target.PinKey);

        // 1) Same LAN, found by broadcast — works without any server.
        var peer = _discovery?.Find(id);
        if (peer is not null && (pinned is null || CertificateFingerprint.AreEqual(pinned, peer.Fingerprint)))
        {
            progress?.Report($"Found on local network ({peer.Address}) — connecting directly…");
            try
            {
                var conn = await PeerConnector.ConnectDirectAsync(_identity, peer.Address.ToString(), peer.Port, pinned ?? peer.Fingerprint, DirectTimeout, ct).ConfigureAwait(false);
                return Verified(conn, id, target);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or HostIdentityMismatchException))
            {
                _logger.LogInformation("LAN connection to {Endpoint} failed: {Message}", peer.Endpoint, ex.Message);
            }
        }

        // 2) Via the self-hosted server.
        if (_signaling is null || !_signaling.IsOnline)
        {
            throw new ConnectException(peer is null
                ? $"Device {DeviceIds.Format(id)} was not found on this network and the server is not connected. Check Settings → Server, or connect by IP address."
                : $"Device {DeviceIds.Format(id)} is on this network but did not answer. It may be blocked by a firewall.");
        }

        progress?.Report("Looking up device on the server…");
        var resolved = await _signaling.ResolveAsync(id, ct).ConfigureAwait(false);
        if (!resolved.Found || resolved.Fingerprint is null)
        {
            throw new ConnectException(resolved.Error ?? $"Device {DeviceIds.Format(id)} is not registered with this server.");
        }

        if (!resolved.Online)
        {
            throw new ConnectException($"Device {DeviceIds.Format(id)} is offline.");
        }

        if (!DeviceIds.TryFromFingerprint(resolved.Fingerprint, out var derived) || derived != id)
        {
            throw new ConnectException("The server returned an inconsistent identity for this device.");
        }

        if (pinned is not null && !CertificateFingerprint.AreEqual(pinned, resolved.Fingerprint))
        {
            throw new HostIdentityMismatchException(
                $"Device {DeviceIds.Format(id)} now has a different identity key than last time. If it was reinstalled, remove it from Devices and try again.");
        }

        string expected = resolved.Fingerprint;

        // 2a) Server-published LAN endpoints (e.g. same office network, broadcast blocked).
        if (resolved.LanEndpoints.Length > 0)
        {
            progress?.Report("Trying direct connection…");
            var direct = await TryDirectEndpointsAsync(resolved.LanEndpoints, expected, ct).ConfigureAwait(false);
            if (direct is not null)
            {
                return Verified(direct, id, target);
            }
        }

        // 2b) Relay through the server (works across NAT/firewalls; still end-to-end TLS).
        progress?.Report("Connecting through relay…");
        var ticket = await _signaling.RequestRelayAsync(id, ct).ConfigureAwait(false);
        if (!ticket.Success || ticket.SessionId is null || ticket.Token is null)
        {
            throw new ConnectException(ticket.Error ?? "The server could not set up a relay.");
        }

        var relayed = await PeerConnector.ConnectRelayAsync(_identity, _signaling.RelayUri(ticket.SessionId), ticket.Token, expected, asTlsServer: false, ct).ConfigureAwait(false);
        return Verified(relayed, id, target);
    }

    private async Task<PeerConnection> ConnectByAddressAsync(
        ConnectTarget target, Func<UnknownHostInfo, Task<bool>> confirmUnknownHost, IProgress<string>? progress, CancellationToken ct)
    {
        string? pinned = _knownHosts.GetFingerprint(target.PinKey);
        progress?.Report($"Connecting to {target}…");
        PeerConnection conn;
        try
        {
            conn = await PeerConnector.ConnectDirectAsync(_identity, target.Host!, target.Port, pinned, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ConnectException($"Could not reach {target}: {ex.Message}", ex);
        }

        if (pinned is null)
        {
            bool trusted;
            try
            {
                trusted = await confirmUnknownHost(new UnknownHostInfo(target.ToString(), conn.RemoteDeviceId, conn.RemoteFingerprint)).ConfigureAwait(false);
            }
            catch
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (!trusted)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new OperationCanceledException("Connection cancelled: device identity not trusted.");
            }

            _knownHosts.Remember(target.PinKey, conn.RemoteFingerprint);
            _knownHosts.Remember("id:" + conn.RemoteDeviceId, conn.RemoteFingerprint);
        }

        return conn;
    }

    private async Task<PeerConnection?> TryDirectEndpointsAsync(string[] endpoints, string expectedFingerprint, CancellationToken ct)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = endpoints.Take(8).Select(async ep =>
        {
            int colon = ep.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(ep[(colon + 1)..], out int port))
            {
                return null;
            }

            try
            {
                return await PeerConnector.ConnectDirectAsync(_identity, ep[..colon].Trim('[', ']'), port, expectedFingerprint, DirectTimeout, raceCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not HostIdentityMismatchException)
            {
                return (PeerConnection?)null;
            }
        }).ToList();

        PeerConnection? winner = null;
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(done);
            var conn = await done.ConfigureAwait(false);
            if (conn is null)
            {
                continue;
            }

            if (winner is null)
            {
                winner = conn;
                raceCts.Cancel();
            }
            else
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }

        return winner;
    }

    private PeerConnection Verified(PeerConnection conn, string expectedId, ConnectTarget target)
    {
        if (conn.RemoteDeviceId != expectedId)
        {
            _ = conn.DisposeAsync();
            throw new HostIdentityMismatchException("Connected device does not own the requested ID.");
        }

        _knownHosts.Remember(target.PinKey, conn.RemoteFingerprint);
        return conn;
    }
}
