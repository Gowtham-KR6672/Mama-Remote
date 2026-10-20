using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RemoteConnect.Protocol;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Security;
using RemoteConnect.Server.Hubs;
using RemoteConnect.Server.Services;

namespace RemoteConnect.Server;

public static class ServerApp
{
    public static string Version { get; } =
        typeof(ServerApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Host.UseWindowsService(o => o.ServiceName = "RemoteConnectServer");

        // PaaS convention (Render, Railway, Heroku, Cloud Run, Fly.io): listen on $PORT when it is provided.
        if (ListenUrlFromPort(Environment.GetEnvironmentVariable("PORT")) is { } portUrl)
        {
            builder.Configuration["Kestrel:Endpoints:Http:Url"] = portUrl;
        }

        configure?.Invoke(builder);

        builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
        builder.Services.AddSingleton<DeviceRegistry>();
        builder.Services.AddSingleton<AuditLog>();
        builder.Services.AddSingleton<RelayBroker>();
        builder.Services.AddSingleton<DeviceRateLimiter>();
        builder.Services.AddSingleton<ConnectionTracker>();

        builder.Services.AddSignalR(o =>
        {
            o.MaximumReceiveMessageSize = 32 * 1024;
            o.KeepAliveInterval = TimeSpan.FromSeconds(15);
            o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
            o.EnableDetailedErrors = builder.Environment.IsDevelopment();
        });

        builder.Services.AddHealthChecks().AddCheck<DataDirectoryHealthCheck>("data-directory", tags: new[] { "ready" });

        var serverOptions = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("per-ip", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(10, serverOptions.ConnectionsPerMinutePerIp),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (serverOptions.TrustForwardedHeaders)
            {
                o.KnownNetworks.Clear();
                o.KnownProxies.Clear();
            }

            foreach (var proxy in serverOptions.TrustedProxies)
            {
                if (IPAddress.TryParse(proxy, out var ip))
                {
                    o.KnownProxies.Add(ip);
                }
            }
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseRateLimiter();

        app.MapHub<SignalHub>(SignalingContract.HubPath).RequireRateLimiting("per-ip");

        app.Map(SignalingContract.RelayPath + "/{sessionId}", (HttpContext ctx, string sessionId, RelayBroker broker) => broker.HandleAsync(ctx, sessionId))
            .RequireRateLimiting("per-ip");

        app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

        app.MapGet("/api/v1/info", (IOptions<ServerOptions> o, DeviceRegistry registry, RelayBroker relay) => Results.Ok(new
        {
            product = "RemoteConnect Server",
            version = Version,
            protocolVersion = ProtocolInfo.Version,
            registrationKeyRequired = !string.IsNullOrEmpty(o.Value.RegistrationKey),
            relay = true,
        }));

        MapAdmin(app);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var o = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
            var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RemoteConnect.Server");
            log.LogInformation("RemoteConnect Server {Version} started. Registration key required: {KeyRequired}. Admin API: {Admin}",
                Version, !string.IsNullOrEmpty(o.RegistrationKey), string.IsNullOrEmpty(o.AdminApiKey) ? "disabled" : "enabled");
            if (string.IsNullOrEmpty(o.RegistrationKey))
            {
                log.LogWarning("No RemoteConnect:RegistrationKey configured — any device that can reach this server can register. Set one for internet-facing deployments.");
            }
        });
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<DeviceRegistry>().SaveIfDirty());

        return app;
    }

    internal static string? ListenUrlFromPort(string? port)
        => int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p)
           && p is > 0 and < 65536
            ? $"http://0.0.0.0:{p}"
            : null;

    private static void MapAdmin(WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var key = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value.AdminApiKey;
            if (string.IsNullOrEmpty(key))
            {
                return Results.NotFound();
            }

            var supplied = ctx.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
            {
                return Results.Unauthorized();
            }

            return await next(ctx);
        }).RequireRateLimiting("per-ip");

        admin.MapGet("/devices", (DeviceRegistry registry) => Results.Ok(registry.All().Select(d => new
        {
            d.DeviceId,
            d.Alias,
            d.Platform,
            d.AppVersion,
            d.Online,
            d.FirstSeen,
            d.LastSeen,
            fingerprint = CertificateFingerprint.ToDisplay(d.Fingerprint),
        })));

        admin.MapDelete("/devices/{deviceId}", (string deviceId, DeviceRegistry registry, ConnectionTracker connections, AuditLog audit) =>
        {
            if (!DeviceIds.TryNormalize(deviceId, out var id) || !registry.Remove(id, out var connectionId))
            {
                return Results.NotFound();
            }

            if (connectionId is not null)
            {
                connections.Abort(connectionId);
            }

            audit.Write("DeviceRevoked", id, detail: "admin API");
            return Results.NoContent();
        });

        admin.MapGet("/audit", (string? deviceId, DateTimeOffset? since, int? limit, AuditLog audit) =>
        {
            string? id = null;
            if (!string.IsNullOrEmpty(deviceId) && !DeviceIds.TryNormalize(deviceId, out id))
            {
                return Results.BadRequest(new { error = "invalid deviceId" });
            }

            return Results.Ok(audit.Query(id, since, limit ?? 200));
        });
    }
}

internal sealed class DataDirectoryHealthCheck(IOptions<ServerOptions> options, IHostEnvironment env) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = DataPaths.Resolve(options.Value.DataDirectory, env);
            var probe = Path.Combine(dir, ".health");
            File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Data directory is not writable.", ex));
        }
    }
}
