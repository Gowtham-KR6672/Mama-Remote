using Avalonia;
using Avalonia.Controls;
using RemoteConnect.Desktop.Services;
using RemoteConnect.Platform;

namespace RemoteConnect.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine($"RemoteConnect {AppHost.AppVersion}");
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // Already running (probably in the tray): bring that window to the front instead.
            if (!args.Contains(LaunchCommand.AutoStartArgument))
            {
                SingleInstance.SignalExisting();
            }

            return 0;
        }

        App.Instance = instance;
        App.StartHidden = args.Contains(LaunchCommand.AutoStartArgument) || args.Contains("--hidden");
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            CrashLog(ex);
            throw;
        }
        finally
        {
            instance.Dispose();
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void CrashLog(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            File.AppendAllText(Path.Combine(AppPaths.LogDirectory, "crash.log"), $"{DateTime.Now:O} {ex}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
