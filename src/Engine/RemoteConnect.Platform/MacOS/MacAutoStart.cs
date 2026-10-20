using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace RemoteConnect.Platform.MacOS;

/// <summary>
/// Start at login with a per-user LaunchAgent (~/Library/LaunchAgents). macOS 13+ lists it under
/// System Settings → General → Login Items, where the user can turn it off.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacAutoStart : IAutoStart
{
    public const string Label = "com.remoteconnect.desktop";

    public bool IsSupported => true;

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public bool IsEnabled() => File.Exists(PlistPath);

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        File.WriteAllText(PlistPath, LaunchAgentPlist.Build(Label, LaunchCommand.Arguments()), new UTF8Encoding(false));
    }
}

/// <summary>LaunchAgent property list (pure string building, testable on any OS).</summary>
public static class LaunchAgentPlist
{
    public static string Build(string label, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        sb.AppendLine("<plist version=\"1.0\">");
        sb.AppendLine("<dict>");
        sb.AppendLine($"  <key>Label</key><string>{label}</string>");
        sb.AppendLine("  <key>ProgramArguments</key>");
        sb.AppendLine("  <array>");
        foreach (var a in arguments)
        {
            sb.AppendLine($"    <string>{SecurityElement.Escape(a)}</string>");
        }

        sb.AppendLine("  </array>");
        sb.AppendLine("  <key>RunAtLoad</key><true/>");
        // Restart after a crash, but not after the user chose Quit (exit code 0).
        sb.AppendLine("  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>");
        sb.AppendLine("  <key>ProcessType</key><string>Interactive</string>");
        sb.AppendLine("  <key>LimitLoadToSessionType</key><string>Aqua</string>");
        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        return sb.ToString();
    }
}
