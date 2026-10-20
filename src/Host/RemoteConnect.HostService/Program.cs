using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using RemoteConnect.HostService;
using RemoteConnect.LocalIpc;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The RemoteConnect host service runs on Windows only.");
    return 1;
}

// Started by an administrator from a prompt: configuration commands. Started by the SCM: the service.
if (args.Length > 0 || !WindowsServiceHelpers.IsWindowsService())
{
    return ServiceCli.Run(args);
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(options => options.ServiceName = ServiceModeLayout.ServiceName);
builder.Services.AddSingleton<ConsoleSessionSignal>();
builder.Services.AddSingleton<IHostLifetime, SessionAwareServiceLifetime>();
builder.Services.AddHostedService<AgentSupervisor>();
builder.Logging.AddFilter<EventLogLoggerProvider>("RemoteConnect", LogLevel.Information);

await builder.Build().RunAsync();
return 0;
