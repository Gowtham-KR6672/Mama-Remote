using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;
using RemoteConnect.Server.Services;

namespace RemoteConnect.Server.Hubs;

/// <summary>
/// Signaling for devices. Every method except GetChallenge/Register requires a registered device, i.e. one that
/// proved possession of the private key behind its ID on this connection.
/// </summary>
public sealed class SignalHub : Hub
{
    private const string ChallengeKey = "rc.challenge";
    private const string DeviceKey = "rc.device";
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(60);

    private readonly DeviceRegistry _registry;
    private readonly RelayBroker _relay;
    private readonly AuditLog _audit;
    private readonly DeviceRateLimiter _limiter;
    private readonly ConnectionTracker _connections;
    private readonly ServerOptions _options;
    private readonly ILogger<SignalHub> _logger;

    public SignalHub(
        DeviceRegistry registry,
        RelayBroker relay,
        AuditLog audit,
        DeviceRateLimiter limiter,
        ConnectionTracker connections,
        IOptions<ServerOptions> options,
        ILogger<SignalHub> logger)
    {
        _registry = registry;
        _relay = relay;
        _audit = audit;
        _limiter = limiter;
        _connections = connections;
        _options = options.Value;
        _logger = logger;
    }

    public static string Group(string deviceId) => "device:" + deviceId;

    public override Task OnConnectedAsync()
    {
        _connections.Add(Context);
        return base.OnConnectedAsync();
    }

    public string GetChallenge()
    {
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Context.Items[ChallengeKey] = (challenge, DateTimeOffset.UtcNow);
        return challenge;
    }

    public async Task<RegisterResponse> Register(RegisterRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        RegisterResponse Fail(RegisterError error, string message)
        {
            _audit.Write("RegisterRejected", request?.DeviceId, address: RemoteAddress(), detail: error.ToString());
            return new RegisterResponse(false, error, message, now);
        }

        if (request is null || request.CertificateBase64 is null || request.SignatureBase64 is null || request.Challenge is null)
        {
            return Fail(RegisterError.InvalidRequest, "Malformed registration.");
        }

        if (!RegistrationKeyMatches(_options.RegistrationKey, request.RegistrationKey))
        {
            return Fail(RegisterError.RegistrationKeyInvalid, "This server requires a valid registration key (Settings → Server).");
        }

        // The challenge is single-use and short-lived: a captured registration cannot be replayed.
        if (Context.Items[ChallengeKey] is not ValueTuple<string, DateTimeOffset> issued
            || issued.Item1 != request.Challenge
            || now - issued.Item2 > ChallengeLifetime)
        {
            return Fail(RegisterError.ChallengeExpired, "Registration challenge expired; retrying.");
        }

        Context.Items.Remove(ChallengeKey);

        if (!DeviceIds.TryNormalize(request.DeviceId, out var deviceId))
        {
            return Fail(RegisterError.InvalidRequest, "Invalid device ID.");
        }

        System.Security.Cryptography.X509Certificates.X509Certificate2 cert;
        byte[] signature;
        try
        {
            cert = PublicCertificate.Load(Convert.FromBase64String(request.CertificateBase64));
            signature = Convert.FromBase64String(request.SignatureBase64);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return Fail(RegisterError.InvalidRequest, "Invalid certificate.");
        }

        using (cert)
        {
            if (PublicCertificate.DeriveDeviceId(cert) != deviceId)
            {
                return Fail(RegisterError.IdMismatch, "Device ID does not match the certificate.");
            }

            if (!PublicCertificate.VerifySignature(cert, PublicCertificate.RegistrationPayload(deviceId, request.Challenge), signature))
            {
                return Fail(RegisterError.BadSignature, "Key possession proof failed.");
            }

            var fingerprint = CertificateFingerprint.Compute(cert);
            var endpoints = SanitizeEndpoints(request.LanEndpoints);
            var result = _registry.Bind(deviceId, fingerprint, Clean(request.Alias, 64), Clean(request.Platform, 16),
                Clean(request.AppVersion, 32), endpoints, Context.ConnectionId, out var previous);
            if (result != RegisterError.None)
            {
                return Fail(result, "This ID is already bound to a different device key.");
            }

            if (previous is not null && previous != Context.ConnectionId)
            {
                await Clients.Client(previous).SendAsync(SignalingContract.SessionReplaced, "Signed in from another connection.").ConfigureAwait(false);
                _connections.Abort(previous);
            }
        }

        Context.Items[DeviceKey] = deviceId;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(deviceId)).ConfigureAwait(false);
        _audit.Write("DeviceOnline", deviceId, address: RemoteAddress(), detail: Clean(request.Platform, 16));
        return new RegisterResponse(true, RegisterError.None, null, now);
    }

    public Task UpdateEndpoints(string[] endpoints)
    {
        var me = RequireDevice();
        _registry.UpdateEndpoints(me, SanitizeEndpoints(endpoints));
        return Task.CompletedTask;
    }

    public ResolveResponse Resolve(string deviceId)
    {
        var me = RequireDevice();
        if (!_limiter.TryAcquire("resolve", me, _options.ResolvePerMinute))
        {
            return new ResolveResponse(false, false, null, null, null, null, Array.Empty<string>(), "Too many lookups; wait a minute.");
        }

        if (!DeviceIds.TryNormalize(deviceId, out var id))
        {
            return new ResolveResponse(false, false, null, null, null, null, Array.Empty<string>(), "Invalid ID.");
        }

        var d = _registry.Get(id);
        if (d is null)
        {
            return new ResolveResponse(false, false, id, null, null, null, Array.Empty<string>(), "No device with this ID is registered on the server.");
        }

        return new ResolveResponse(true, d.Online, id, d.Fingerprint, d.Alias, d.Platform, d.Online ? d.LanEndpoints : Array.Empty<string>(), null);
    }

    public async Task<RelayTicket> RequestRelay(string deviceId)
    {
        var me = RequireDevice();
        if (!_limiter.TryAcquire("relay", me, _options.RelayRequestsPerMinute))
        {
            return new RelayTicket(false, null, null, null, "Too many connection attempts; wait a minute.");
        }

        if (!DeviceIds.TryNormalize(deviceId, out var target) || target == me)
        {
            return new RelayTicket(false, null, null, null, "Invalid target.");
        }

        var host = _registry.Get(target);
        var self = _registry.Get(me);
        if (host is null || !host.Online || self is null)
        {
            return new RelayTicket(false, null, null, null, "The device is offline.");
        }

        var created = _relay.Create(me, target);
        if (created is null)
        {
            return new RelayTicket(false, null, null, null, "The relay is at capacity; try again shortly.");
        }

        var (sessionId, controllerToken, hostToken) = created.Value;
        await Clients.Group(Group(target)).SendAsync(
            SignalingContract.RelayOffer,
            new RelayOffer(sessionId, hostToken, me, self.Fingerprint, self.Alias)).ConfigureAwait(false);
        _audit.Write("RelayRequested", me, target, sessionId, RemoteAddress());
        return new RelayTicket(true, sessionId, controllerToken, host.Fingerprint, null);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Remove(Context.ConnectionId);
        if (Context.Items[DeviceKey] is string id && _registry.SetOffline(id, Context.ConnectionId))
        {
            _audit.Write("DeviceOffline", id, address: RemoteAddress());
        }

        return base.OnDisconnectedAsync(exception);
    }

    private string RequireDevice()
        => Context.Items[DeviceKey] as string ?? throw new HubException("Register first.");

    private string? RemoteAddress()
        => Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();

    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    internal static string[] SanitizeEndpoints(string[]? endpoints)
    {
        if (endpoints is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var e in endpoints.Take(8))
        {
            if (e is null || e.Length > 64)
            {
                continue;
            }

            int colon = e.LastIndexOf(':');
            if (colon > 0 && IPAddress.TryParse(e[..colon].Trim('[', ']'), out var ip)
                && int.TryParse(e[(colon + 1)..], out var port) && port is > 0 and < 65536)
            {
                result.Add(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}");
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Keys are compared without surrounding whitespace: values pasted into a hosting dashboard (or copied from a
    /// terminal) often carry a stray newline or space. No configured key = registration is open.
    /// </summary>
    internal static bool RegistrationKeyMatches(string? configured, string? supplied)
    {
        var expected = configured?.Trim();
        return string.IsNullOrEmpty(expected) || FixedTimeEquals(expected, supplied?.Trim() ?? string.Empty);
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}

/// <summary>Lets the server abort a superseded or revoked device connection.</summary>
public sealed class ConnectionTracker
{
    private readonly ConcurrentDictionary<string, HubCallerContext> _contexts = new();

    public void Add(HubCallerContext context) => _contexts[context.ConnectionId] = context;

    public void Remove(string connectionId) => _contexts.TryRemove(connectionId, out _);

    public void Abort(string connectionId)
    {
        if (_contexts.TryRemove(connectionId, out var ctx))
        {
            ctx.Abort();
        }
    }
}
