using System.Diagnostics;
using System.Runtime.Versioning;
using RemoteConnect.LocalIpc.Windows;
using RemoteConnect.Platform;
using RemoteConnect.Platform.Windows;

namespace RemoteConnect.Desktop.Services;

/// <summary>
/// Removes this copy of RemoteConnect: start at sign-in, the "Installed apps" entry, shortcuts, optionally the
/// settings and device identity, and finally the program itself. Used by Windows "Installed apps → Uninstall"
/// (<c>RemoteConnect.exe --uninstall</c>) and by Settings → Uninstall.
/// </summary>
public static class Uninstaller
{
    public const string Argument = "--uninstall";
    private const string Caption = "Uninstall RemoteConnect";

    [SupportedOSPlatform("windows")]
    public static int RunFromCommandLine(string[] args)
    {
        bool quiet = args.Contains("--quiet");
        if (ServiceStatusRegistry.IsServiceInstalled())
        {
            if (!quiet)
            {
                WindowsShell.Inform(
                    "RemoteConnect is installed in service mode on this computer.\n\nRemove it from Windows Settings → Apps → Installed apps, or run uninstall-windows.ps1 as administrator.",
                    Caption);
            }

            return 1;
        }

        if (!quiet && !WindowsShell.Confirm(
                "Remove RemoteConnect from this computer?\n\nIt will stop starting at sign-in, and nobody can connect to this computer any more.",
                Caption))
        {
            return 1;
        }

        bool removeData = args.Contains("--remove-data") || (!quiet && WindowsShell.Confirm(
            "Also delete RemoteConnect's settings and this computer's ID?\n\nChoose No to keep them, so this computer gets the same ID if RemoteConnect is installed again.",
            Caption));

        StopRunningCopy();
        Remove(removeData);
        DeleteProgramFiles();
        if (!quiet)
        {
            WindowsShell.Inform("RemoteConnect has been removed from this computer.", Caption);
        }

        return 0;
    }

    /// <summary>Asks a copy running in the tray to quit (ending its sessions) and waits until it has.</summary>
    public static void StopRunningCopy()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using (var probe = SingleInstance.TryAcquire())
            {
                if (probe is not null)
                {
                    return; // nothing else running
                }
            }

            SingleInstance.SignalExisting(SingleInstance.QuitCommand);
            Thread.Sleep(500);
        }
    }

    /// <summary>Removes start at sign-in, the Installed-apps entry, shortcuts and (optionally) user data.</summary>
    public static void Remove(bool removeData)
    {
        try
        {
            PlatformServices.AutoStart.SetEnabled(false);
        }
        catch (Exception)
        {
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                WindowsShell.UnregisterInstalledApp();
            }
            catch (Exception)
            {
            }

            TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RemoteConnect.lnk"));
            TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RemoteConnect.lnk"));
        }

        if (removeData)
        {
            try
            {
                Directory.Delete(AppPaths.ConfigDirectory, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// Deletes the program after this process exits: the whole folder of a scripted install
    /// (%LOCALAPPDATA%\Programs\RemoteConnect), only RemoteConnect.exe for a copy that was just run as a single
    /// file, or the RemoteConnect.app bundle on macOS. Never touches anything when running from a build folder.
    /// </summary>
    public static void DeleteProgramFiles()
    {
        var exe = Environment.ProcessPath;
        if (!IsDeployedCopy(exe))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var dir = Path.GetDirectoryName(exe)!;
            var scripted = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RemoteConnect");
            bool ownFolder = string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(scripted).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            WindowsShell.DeleteAfterExit(ownFolder ? dir : exe!, isDirectory: ownFolder);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // …/RemoteConnect.app/Contents/MacOS/RemoteConnect.Desktop
            var bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", ".."));
            if (bundle.EndsWith("RemoteConnect.app", StringComparison.Ordinal))
            {
                var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("sleep 3; rm -rf \"$0\"");
                psi.ArgumentList.Add(bundle);
                Process.Start(psi);
            }
        }
    }

    /// <summary>A real deployed copy (not "dotnet" and not a bin\Debug or bin\Release build folder).</summary>
    public static bool IsDeployedCopy(string? exe)
    {
        if (string.IsNullOrEmpty(exe) || !Path.GetFileName(exe).StartsWith("RemoteConnect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sep = Path.DirectorySeparatorChar;
        return !exe.Contains($"{sep}bin{sep}Debug{sep}", StringComparison.OrdinalIgnoreCase)
            && !exe.Contains($"{sep}bin{sep}Release{sep}", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
