using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using RemoteConnect.Desktop.Services;
using RemoteConnect.LocalIpc;
using RemoteConnect.LocalIpc.Windows;

namespace RemoteConnect.Desktop.Agent;

/// <summary>
/// <c>RemoteConnect.Desktop.exe --agent</c>: the session agent the Windows service starts in the console session.
/// It refuses to run unless it is LocalSystem and can reach the genuine service over its pipe, and it exits as soon
/// as that pipe closes, so it never outlives (or runs without) the service.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AgentMode
{
    public const int ExitNotLocalSystem = 2;
    public const int ExitNoService = 3;

    private static IpcChannel? _service;

    public static bool IsActive => _service is not null;

    public static int Run(string[] args)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!identity.IsSystem)
            {
                return ExitNotLocalSystem; // started by hand: the agent only ever runs under the service
            }
        }

        AppPaths.UseDirectory(ServiceModeLayout.DataDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Program.CrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        try
        {
            _service = ServicePipe.ConnectAsync(
                ServiceModeLayout.PipeName,
                ServiceModeLayout.ServicePath(AppContext.BaseDirectory),
                requireLocalSystem: true,
                TimeSpan.FromSeconds(15),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or IpcPeerRejectedException or Win32Exception)
        {
            Program.CrashLog(new InvalidOperationException("Session agent could not reach the RemoteConnect service: " + ex.Message, ex));
            return ExitNoService;
        }

        return Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Called by <see cref="App"/> once Avalonia is running (needed for the session indicator).</summary>
    public static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // No Attach(): the agent has no per-user settings or history; it only shows the always-visible session panel.
        var host = AgentHost.Create(_service!, new HostInteraction());
        _ = Task.Run(async () =>
        {
            try
            {
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                host.Logger.LogError(ex, "Session agent failed");
            }
            finally
            {
                await host.DisposeAsync();
                Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
            }
        });
    }
}
