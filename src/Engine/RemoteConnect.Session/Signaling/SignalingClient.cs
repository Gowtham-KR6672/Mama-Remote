using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;

namespace RemoteConnect.Session.Signaling;

public enum SignalingStatus
{
    Disabled,
    Connecting,
    Online,
    Offline,
    Rejected,
}

public sealed record SignalingOptions(string? ServerUrl, string? RegistrationKey, string Alias, string Platform, string AppVersion);

/// <summary>
/// Keeps this device registered with the self-hosted server: proves possession of the device key, publishes
/// LAN endpoints, resolves other IDs and brokers relayed sessions. Reconnects with backoff.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    private readonly DeviceIdentity _identity;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private HubConnection? _hub;

    public SignalingClient(DeviceIdentity identity, ILogger logger)
    {
        _identity = identity;
        _logger = logger;
    }

    public SignalingStatus Status { get; private set; } = SignalingStatus.Disabled;

    public string? StatusMessage { get; private set; }

    public Uri? ServerUri { get; private set; }

    public RegisterError LastRegisterError { get; private set; }

    public event Action? StatusChanged;

    /// <summary>A controller wants to reach this device through the relay.</summary>
    public Func<RelayOffer, Task>? RelayOfferHandler { get; set; }

    public Func<string[]> LanEndpointsProvider { get; set; } = Array.Empty<string>;

    public bool IsOnline => Status == SignalingStatus.Online && _hub is not null;

    /// <summary>(Re)starts the connection loop with new settings. Null/empty server disables signaling.</summary>
    public async Task ConfigureAsync(SignalingOptions options)
    {
        await StopAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(options.ServerUrl))
        {
            ServerUri = null;
            SetStatus(SignalingStatus.Disabled, "No server configured — LAN only.");
            return;
        }

        if (!ServerAddress.TryParse(options.ServerUrl, out var uri, out var error))
        {
            ServerUri = null;
            SetStatus(SignalingStatus.Rejected, error);
            return;
        }

        ServerUri = uri;
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _runCts = cts;
            _runTask = Task.Run(() => RunAsync(uri, options, cts.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            cts = _runCts;
            task = _runTask;
            _runCts = null;
            _runTask = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        cts.Dispose();
    }

    public async Task<ResolveResponse> ResolveAsync(string deviceId, CancellationToken ct)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected to the server.");
        return await hub.InvokeAsync<ResolveResponse>(SignalingContract.Resolve, deviceId, ct).ConfigureAwait(false);
    }

    public async Task<RelayTicket> RequestRelayAsync(string deviceId, CancellationToken ct)
    {
        var hub = _hub ?? throw new InvalidOperationException("Not connected to the server.");
        return await hub.InvokeAsync<RelayTicket>(SignalingContract.RequestRelay, deviceId, ct).ConfigureAwait(false);
    }

    public Uri RelayUri(string sessionId)
        => ServerAddress.RelayUri(ServerUri ?? throw new InvalidOperationException("No server configured."), sessionId);

    public async Task PublishEndpointsAsync()
    {
        var hub = _hub;
        if (hub is null)
        {
            return;
        }

        try
        {
            await hub.InvokeAsync(SignalingContract.UpdateEndpoints, LanEndpointsProvider()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Endpoint update failed: {Message}", ex.Message);
        }
    }

    private async Task RunAsync(Uri server, SignalingOptions options, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            HubConnection? hub = null;
            try
            {
                SetStatus(SignalingStatus.Connecting, $"Connecting to {server.Host}…");
                hub = new HubConnectionBuilder()
                    .WithUrl(new Uri(server, SignalingContract.HubPath), http =>
                    {
                        http.Transports = HttpTransportType.WebSockets;
                        http.SkipNegotiation = true;
                    })
                    .Build();
                hub.ServerTimeout = TimeSpan.FromSeconds(60);
                hub.KeepAliveInterval = TimeSpan.FromSeconds(15);

                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                hub.Closed += ex =>
                {
                    closed.TrySetResult(ex);
                    return Task.CompletedTask;
                };
                hub.On<RelayOffer>(SignalingContract.RelayOffer, offer =>
                {
                    var handler = RelayOfferHandler;
                    if (handler is not null)
                    {
                        _ = Task.Run(() => handler(offer));
                    }
                });
                hub.On<string>(SignalingContract.SessionReplaced, message =>
                {
                    _logger.LogWarning("Server replaced this registration: {Message}", message);
                });

                await hub.StartAsync(ct).ConfigureAwait(false);
                var challenge = await hub.InvokeAsync<string>(SignalingContract.GetChallenge, ct).ConfigureAwait(false);
                var signature = _identity.Sign(PublicCertificate.RegistrationPayload(_identity.DeviceId, challenge));
                var request = new RegisterRequest(
                    _identity.DeviceId,
                    Convert.ToBase64String(_identity.ExportPublicCertificate()),
                    challenge,
                    Convert.ToBase64String(signature),
                    options.Alias,
                    options.Platform,
                    options.AppVersion,
                    string.IsNullOrEmpty(options.RegistrationKey) ? null : options.RegistrationKey,
                    LanEndpointsProvider());
                var response = await hub.InvokeAsync<RegisterResponse>(SignalingContract.Register, request, ct).ConfigureAwait(false);
                LastRegisterError = response.Error;
                if (!response.Success)
                {
                    SetStatus(SignalingStatus.Rejected, response.Message ?? $"Server refused registration ({response.Error}).");
                    await hub.DisposeAsync().ConfigureAwait(false);
                    hub = null;
                    // Configuration problems will not fix themselves quickly.
                    await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                    continue;
                }

                _hub = hub;
                backoff = TimeSpan.FromSeconds(2);
                SetStatus(SignalingStatus.Online, $"Connected to {server.Host}");

                using var periodic = new PeriodicTimer(TimeSpan.FromSeconds(60));
                var closedTask = closed.Task;
                while (!ct.IsCancellationRequested)
                {
                    var tick = periodic.WaitForNextTickAsync(ct).AsTask();
                    if (await Task.WhenAny(closedTask, tick).ConfigureAwait(false) == closedTask)
                    {
                        break;
                    }

                    await PublishEndpointsAsync().ConfigureAwait(false);
                }

                _hub = null;
                if (!ct.IsCancellationRequested)
                {
                    var ex = closedTask.IsCompleted ? closedTask.Result : null;
                    SetStatus(SignalingStatus.Offline, "Lost connection to the server" + (ex is null ? "." : $": {ex.Message}"));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _hub = null;
                SetStatus(SignalingStatus.Offline, $"Server unreachable: {ex.Message}");
            }
            finally
            {
                if (hub is not null)
                {
                    _hub = null;
                    try
                    {
                        await hub.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }

        _hub = null;
        SetStatus(SignalingStatus.Disabled, null);
    }

    private void SetStatus(SignalingStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
        try
        {
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status handler failed");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
