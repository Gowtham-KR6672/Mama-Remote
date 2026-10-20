using RemoteConnect.Server;

if (args.Contains("--version"))
{
    Console.WriteLine($"RemoteConnect Server {ServerApp.Version}");
    return;
}

var app = ServerApp.Build(args);
await app.RunAsync();
