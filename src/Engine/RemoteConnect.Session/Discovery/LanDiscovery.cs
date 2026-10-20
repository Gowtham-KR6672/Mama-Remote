using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteConnect.Protocol;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;

namespace RemoteConnect.Session.Discovery;

public sealed record LanPeer(string DeviceId, string Alias, string Platform, IPAddress Address, int Port, string Fingerprint, DateTimeOffset LastSeen)
{
    public string Endpoint => $"{Address}:{Port}";
}

/// <summary>
/// Finds RemoteConnect devices on the local network via UDP broadcast — works with no server at all.
/// Announcements are unauthenticated hints: the (ID, fingerprint) pair must be self-consistent and the later TLS
/// handshake is pinned to that fingerprint, so a spoofed announcement cannot impersonate a device.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PeerExpiry = TimeSpan.FromSeconds(20);
    private const int MaxPacket = 2048;

    private readonly string _ownId;
    private readonly ILogger _logger;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, LanPeer> _peers = new();
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;

    public LanDiscovery(string ownDeviceId, ILogger logger, int port = LanAnnouncement.DefaultPort)
    {
        _ownId = ownDeviceId;
        _logger = logger;
        _port = port;
    }

    public event Action? PeersChanged;

    public bool IsRunning => _cts is not null;

    public string? Error { get; private set; }

    public IReadOnlyList<LanPeer> Peers => _peers.Values.OrderBy(p => p.Alias, StringComparer.OrdinalIgnoreCase).ToList();

    public LanPeer? Find(string deviceId)
        => _peers.TryGetValue(deviceId, out var p) && DateTimeOffset.UtcNow - p.LastSeen < PeerExpiry ? p : null;

    /// <param name="announcement">Returns what to broadcast, or null to stay silent (e.g. incoming connections disabled).</param>
    public void Start(Func<LanAnnouncement?> announcement)
    {
        Stop();
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            _udp = udp;
            _cts = new CancellationTokenSource();
            Error = null;
            _ = ReceiveLoopAsync(udp, _cts.Token);
            _ = AnnounceLoopAsync(udp, announcement, _cts.Token);
        }
        catch (SocketException ex)
        {
            Error = $"LAN discovery unavailable (UDP {_port}): {ex.Message}";
            _logger.LogWarning("{Error}", Error);
            Stop();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _udp?.Dispose();
        _udp = null;
        if (!_peers.IsEmpty)
        {
            _peers.Clear();
            PeersChanged?.Invoke();
        }
    }

    private async Task AnnounceLoopAsync(UdpClient udp, Func<LanAnnouncement?> announcement, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var a = announcement();
                if (a is not null)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(a, MessageFramer.Json);
                    var targets = NetworkInfo.GetLocalIPv4().Select(l => l.Broadcast).OfType<IPAddress>()
                        .Append(IPAddress.Broadcast).Distinct();
                    foreach (var target in targets)
                    {
                        try
                        {
                            await udp.SendAsync(bytes, new IPEndPoint(target, _port), ct).ConfigureAwait(false);
                        }
                        catch (SocketException)
                        {
                            // Some interfaces refuse broadcast; others still work.
                        }
                    }
                }

                bool removed = false;
                foreach (var (id, peer) in _peers)
                {
                    if (DateTimeOffset.UtcNow - peer.LastSeen > PeerExpiry && _peers.TryRemove(id, out _))
                    {
                        removed = true;
                    }
                }

                if (removed)
                {
                    PeersChanged?.Invoke();
                }

                await Task.Delay(AnnounceInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (packet.Buffer.Length > MaxPacket)
            {
                continue;
            }

            var peer = Parse(packet.Buffer, packet.RemoteEndPoint.Address);
            if (peer is null || peer.DeviceId == _ownId)
            {
                continue;
            }

            bool isNew = !_peers.ContainsKey(peer.DeviceId);
            _peers[peer.DeviceId] = peer;
            if (isNew)
            {
                PeersChanged?.Invoke();
            }
        }
    }

    internal static LanPeer? Parse(byte[] buffer, IPAddress sender)
    {
        LanAnnouncement? a;
        try
        {
            a = JsonSerializer.Deserialize<LanAnnouncement>(buffer, MessageFramer.Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (a is null || a.V != 1 || a.Port is <= 0 or > 65535
            || !DeviceIds.TryNormalize(a.DeviceId, out var id)
            || !DeviceIds.TryFromFingerprint(a.Fingerprint, out var derived) || derived != id)
        {
            return null;
        }

        string alias = Sanitize(a.Alias, 64);
        string platform = Sanitize(a.Platform, 16);
        return new LanPeer(id, alias.Length == 0 ? DeviceIds.Format(id) : alias, platform, sender, a.Port, a.Fingerprint.ToUpperInvariant(), DateTimeOffset.UtcNow);
    }

    private static string Sanitize(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    public void Dispose() => Stop();
}
