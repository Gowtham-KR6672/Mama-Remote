using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using RemoteConnect.Protocol.Signaling;
using RemoteConnect.Server;
using RemoteConnect.Server.Services;

namespace RemoteConnect.IntegrationTests;

/// <summary>The relay on its own: pairing, byte forwarding in both directions, token checks.</summary>
public class RelayBrokerTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "rc-relay-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _server;
    private Uri? _ws;

    public async Task InitializeAsync()
    {
        _server = ServerApp.Build(new[]
        {
            "--Kestrel:Endpoints:Http:Url=http://127.0.0.1:0",
            $"--RemoteConnect:DataDirectory={_dataDir}",
            "--Logging:LogLevel:Default=Warning",
        });
        await _server.StartAsync();
        var http = new Uri(_server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        _ws = new UriBuilder("ws", http.Host, http.Port).Uri;
    }

    public async Task DisposeAsync()
    {
        await _server!.StopAsync();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_dataDir, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<ClientWebSocket> Join(string sessionId, string token)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(SignalingContract.RelayTokenHeader, token);
        await ws.ConnectAsync(new Uri(_ws!, $"/relay/{sessionId}"), default);
        return ws;
    }

    private static async Task<string> ReceiveText(ClientWebSocket ws)
    {
        var buffer = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var r = await ws.ReceiveAsync(buffer, cts.Token);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, r.Count);
    }

    [Fact]
    public async Task Relay_forwards_bytes_both_ways_after_pairing()
    {
        var broker = _server!.Services.GetRequiredService<RelayBroker>();
        var (id, controllerToken, hostToken) = broker.Create("111111111", "222222222")!.Value;

        using var controller = await Join(id, controllerToken);
        await controller.SendAsync("hello-before-pairing"u8.ToArray(), WebSocketMessageType.Binary, true, default);
        using var host = await Join(id, hostToken);

        Assert.Equal("hello-before-pairing", await ReceiveText(host));
        await host.SendAsync("reply"u8.ToArray(), WebSocketMessageType.Binary, true, default);
        Assert.Equal("reply", await ReceiveText(controller));
    }

    [Fact]
    public async Task Relay_works_when_host_joins_first()
    {
        var broker = _server!.Services.GetRequiredService<RelayBroker>();
        var (id, controllerToken, hostToken) = broker.Create("111111111", "222222222")!.Value;

        using var host = await Join(id, hostToken);
        var hostReceive = ReceiveText(host);
        await Task.Delay(200);
        using var controller = await Join(id, controllerToken);
        await controller.SendAsync("client-hello"u8.ToArray(), WebSocketMessageType.Binary, true, default);

        Assert.Equal("client-hello", await hostReceive);
        await host.SendAsync("server-hello"u8.ToArray(), WebSocketMessageType.Binary, true, default);
        Assert.Equal("server-hello", await ReceiveText(controller));
    }

    [Theory]
    [InlineData("10000", "http://0.0.0.0:10000")]
    [InlineData("8080", "http://0.0.0.0:8080")]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("70000", null)]
    [InlineData("80; rm -rf", null)]
    public void Paas_port_variable_maps_to_listen_address(string port, string? expected)
        => Assert.Equal(expected, ServerApp.ListenUrlFromPort(port));

    [Theory]
    [InlineData("secret-key", "secret-key", true)]
    [InlineData("secret-key\n", "secret-key", true)]       // pasted into a dashboard with a trailing newline
    [InlineData("  secret-key ", "secret-key\r\n", true)]
    [InlineData("secret-key", "secret-kez", false)]
    [InlineData("secret-key", "", false)]
    [InlineData("secret-key", null, false)]
    [InlineData("", "anything", true)]                     // no key configured = open registration
    [InlineData("   ", null, true)]
    public void Registration_key_comparison_ignores_surrounding_whitespace(string configured, string? supplied, bool expected)
        => Assert.Equal(expected, RemoteConnect.Server.Hubs.SignalHub.RegistrationKeyMatches(configured, supplied));

    [Fact]
    public async Task Relay_rejects_wrong_or_reused_tokens()
    {
        var broker = _server!.Services.GetRequiredService<RelayBroker>();
        var (id, controllerToken, _) = broker.Create("111111111", "222222222")!.Value;

        await Assert.ThrowsAsync<WebSocketException>(() => Join(id, "not-the-token"));
        using var first = await Join(id, controllerToken);
        await Assert.ThrowsAsync<WebSocketException>(() => Join(id, controllerToken)); // single use
        await Assert.ThrowsAsync<WebSocketException>(() => Join("unknown-session", controllerToken));
    }
}
