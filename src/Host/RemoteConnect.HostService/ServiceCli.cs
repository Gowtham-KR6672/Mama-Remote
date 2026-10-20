using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using RemoteConnect.LocalIpc;
using RemoteConnect.LocalIpc.Windows;
using RemoteConnect.Security;

namespace RemoteConnect.HostService;

/// <summary>
/// Administrator commands for the service-mode host (the same executable, run from an elevated prompt). This is the
/// only way to turn on unattended access in service mode; the service itself never reads commands from anywhere.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceCli
{
    private const int Ok = 0;
    private const int Usage = 1;
    private const int NeedsAdmin = 2;
    private const int Failed = 3;

    public static int Run(string[] args)
    {
        string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();
        try
        {
            return command switch
            {
                "status" => Status(),
                "set-password" => RequireAdmin() ?? SetPassword(),
                "disable-unattended" => RequireAdmin() ?? Update(s =>
                {
                    s.UnattendedEnabled = false;
                    s.UnattendedPasswordHash = null;
                }, "Unattended access is OFF. Nobody can connect to this computer through the service."),
                "allow" => RequireAdmin() ?? Allow(rest),
                "allow-control" => RequireAdmin() ?? AllowControl(rest),
                "set-server" => RequireAdmin() ?? SetServer(rest),
                "set-registration-key" => RequireAdmin() ?? SetRegistrationKey(rest),
                "set-alias" => RequireAdmin() ?? SetAlias(rest),
                "--version" or "version" => Print($"RemoteConnect host service {typeof(ServiceCli).Assembly.GetName().Version?.ToString(3)}"),
                "help" or "--help" or "-h" or "/?" => Help(),
                _ => Error($"Unknown command '{args[0]}'.", Usage, showHelp: true),
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error(ex.Message + " Run this from an elevated (administrator) prompt.", NeedsAdmin);
        }
        catch (IOException ex)
        {
            return Error(ex.Message, Failed);
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            RemoteConnect host service (service mode: reachable at the Windows sign-in screen).
            This program runs as a Windows service. Run it from an elevated prompt to configure it:

              status                       Service and agent state, this computer's device ID, unattended access.
              set-password                 Set the unattended-access password (asked for twice) and turn unattended access ON.
              disable-unattended           Turn unattended access OFF and delete the password hash.
              allow <ID>[,<ID>...]         Only these controller IDs may use the password.  "allow any" removes the limit.
              allow-control on|off         Unattended sessions may control the computer (on) or only view it (off).
              set-server <URL>|none        Self-hosted RemoteConnect server for this computer's service ID.
              set-registration-key [--clear]  Server registration key (asked for; stored DPAPI-protected).
              set-alias <name>             Name shown to people who connect.

            In service mode nobody can accept a request at the sign-in screen, so the unattended password is the only
            way in. Unattended access stays OFF until you run set-password. Changes apply within a few seconds.
            """);
        return Ok;
    }

    private static int Status()
    {
        string service;
        try
        {
            using var controller = new ServiceController(ServiceModeLayout.ServiceName);
            service = $"{controller.Status} ({controller.StartType})";
        }
        catch (InvalidOperationException)
        {
            service = "not installed";
        }

        Console.WriteLine($"Service          : {ServiceModeLayout.ServiceName} — {service}");
        var status = ServiceStatusRegistry.TryRead();
        if (status is null)
        {
            Console.WriteLine("Agent            : no status published yet");
        }
        else
        {
            Console.WriteLine($"Agent            : {status.State}" + (status.ConsoleSessionId is uint s ? $" (console session {s})" : string.Empty)
                + (status.InputDesktop is { } d ? $", desktop {d}" : string.Empty));
            Console.WriteLine($"Device ID        : {(status.DeviceId is { } id ? DeviceIds.Format(id) : "(not known yet)")}");
            Console.WriteLine($"Unattended access: {(status.UnattendedReady ? "ON (password set)" : "OFF")}");
            Console.WriteLine($"Updated          : {status.Updated.ToLocalTime():g}");
        }

        if (IsAdministrator())
        {
            var settings = ServiceModeSettingsFile.Load(ServiceModeLayout.SettingsFile, out var error);
            Console.WriteLine($"Settings file    : {ServiceModeLayout.SettingsFile}{(error is null ? string.Empty : $" (unreadable: {error})")}");
            Console.WriteLine($"  alias          : {settings.Alias}");
            Console.WriteLine($"  server         : {settings.ServerUrl ?? "(none — LAN only)"}");
            Console.WriteLine($"  LAN            : listener {(settings.LanListenerEnabled ? $"on, port {settings.LanPort}" : "off")}, discovery {(settings.LanDiscoveryEnabled ? "on" : "off")}");
            Console.WriteLine($"  unattended     : {(settings.UnattendedReady ? "ON" : "OFF")}, control {(settings.UnattendedAllowControl ? "allowed" : "view only")}, "
                + (settings.UnattendedAllowList.Count == 0 ? "any device with the password" : "only " + string.Join(", ", settings.UnattendedAllowList.Select(DeviceIds.Format))));
            Console.WriteLine($"Logs             : {ServiceModeLayout.LogDirectory} (agent), Windows Event Log → Application (service)");
        }

        return Ok;
    }

    private static int SetPassword()
    {
        string? password;
        if (Console.IsInputRedirected)
        {
            password = Console.ReadLine();
        }
        else
        {
            password = ReadSecret("New unattended-access password: ");
            if (password != ReadSecret("Repeat the password: "))
            {
                return Error("The passwords do not match. Nothing was changed.", Usage);
            }
        }

        string hash;
        try
        {
            hash = PasswordHasher.Hash(password ?? string.Empty);
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message + " Nothing was changed.", Usage);
        }

        return Update(s =>
        {
            s.UnattendedPasswordHash = hash;
            s.UnattendedEnabled = true;
        }, "Unattended access is ON. People who know this password can connect to this computer at any time, including at the sign-in screen.");
    }

    private static int Allow(string[] args)
    {
        if (args.Length == 0)
        {
            return Error("Usage: allow <ID>[,<ID>...] | allow any", Usage);
        }

        if (args.Length == 1 && args[0].Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return Update(s => s.UnattendedAllowList.Clear(), "Any device that knows the password may connect.");
        }

        var ids = new List<string>();
        foreach (var part in string.Join(',', args).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!DeviceIds.TryNormalize(part, out var id))
            {
                return Error($"'{part}' is not a 9-digit device ID. Nothing was changed.", Usage);
            }

            ids.Add(id);
        }

        return Update(s => s.UnattendedAllowList = ids.Distinct().ToList(), "Only these devices may use the password: " + string.Join(", ", ids.Distinct().Select(DeviceIds.Format)));
    }

    private static int AllowControl(string[] args)
    {
        bool? on = args.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => null,
        };
        return on is bool value
            ? Update(s => s.UnattendedAllowControl = value, value ? "Unattended sessions may control this computer." : "Unattended sessions are view-only.")
            : Error("Usage: allow-control on|off", Usage);
    }

    private static int SetServer(string[] args)
    {
        var url = args.FirstOrDefault();
        if (url is null)
        {
            return Error("Usage: set-server <URL>|none", Usage);
        }

        if (url.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return Update(s => s.ServerUrl = null, "No server: the service is reachable on the local network only.");
        }

        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return Error($"'{args[0]}' is not a server address.", Usage);
        }

        return Update(s => s.ServerUrl = uri.ToString(), $"Server set to {uri}.");
    }

    private static int SetRegistrationKey(string[] args)
    {
        if (args.FirstOrDefault() == "--clear")
        {
            if (File.Exists(ServiceModeLayout.RegistrationKeyFile))
            {
                File.Delete(ServiceModeLayout.RegistrationKeyFile);
            }

            return Print("Registration key removed.");
        }

        var key = Console.IsInputRedirected ? Console.ReadLine() : ReadSecret("Server registration key: ");
        if (string.IsNullOrWhiteSpace(key))
        {
            return Error("Empty key. Nothing was changed.", Usage);
        }

        // LocalMachine DPAPI: written by an administrator, read by the LocalSystem agent; the folder ACL keeps users out.
        ProtectedFileStore.Write(ServiceModeLayout.RegistrationKeyFile, Encoding.UTF8.GetBytes(key.Trim()), machineScope: true);
        return Print("Registration key saved.");
    }

    private static int SetAlias(string[] args)
    {
        var alias = string.Join(' ', args).Trim();
        return alias.Length == 0
            ? Error("Usage: set-alias <name>", Usage)
            : Update(s => s.Alias = alias, $"Alias set to '{alias}'.");
    }

    private static int Update(Action<ServiceModeSettings> change, string done)
    {
        var settings = ServiceModeSettingsFile.Load(ServiceModeLayout.SettingsFile, out var error);
        if (error is not null)
        {
            Console.Error.WriteLine($"Warning: the existing settings could not be read ({error}); starting from defaults.");
        }

        change(settings);
        ServiceModeSettingsFile.Save(ServiceModeLayout.SettingsFile, settings);
        return Print(done);
    }

    private static int? RequireAdmin()
        => IsAdministrator() ? null : Error("This command changes machine-wide settings. Run it from an elevated (administrator) prompt.", NeedsAdmin);

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return sb.ToString();
    }

    private static int Print(string message)
    {
        Console.WriteLine(message);
        return Ok;
    }

    private static int Error(string message, int code, bool showHelp = false)
    {
        Console.Error.WriteLine(message);
        if (showHelp)
        {
            Console.Error.WriteLine("Run with 'help' for the list of commands.");
        }

        return code;
    }
}
