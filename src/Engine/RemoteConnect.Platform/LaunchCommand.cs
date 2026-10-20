namespace RemoteConnect.Platform;

/// <summary>Builds the command line used to relaunch this application at sign-in.</summary>
public static class LaunchCommand
{
    public const string AutoStartArgument = "--autostart";

    public static string ExecutablePath() => Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");

    /// <summary>When running as "dotnet RemoteConnect.Desktop.dll" (dev), the dll must be passed too.</summary>
    public static IReadOnlyList<string> Arguments()
    {
        var exe = ExecutablePath();
        var args = new List<string> { exe };
        var name = Path.GetFileNameWithoutExtension(exe);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(entry))
            {
                args.Add(entry);
            }
        }

        args.Add(AutoStartArgument);
        return args;
    }

    public static string ForWindowsRunKey() => string.Join(' ', Arguments().Select(a => a.StartsWith("--", StringComparison.Ordinal) ? a : $"\"{a}\""));
}
