using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;
using RemoteConnect.Security;

namespace RemoteConnect.LocalIpc.Windows;

/// <summary>What the service publishes about itself. Nothing secret: the device ID is an address.</summary>
public sealed record ServicePublicStatus(
    string State,
    uint? ConsoleSessionId,
    string? DeviceId,
    bool UnattendedReady,
    string? InputDesktop,
    DateTimeOffset Updated);

/// <summary>
/// Public status under HKLM (written by the service as LocalSystem, readable by every local user). The desktop app
/// reads it to show the service's device ID; standard users cannot write HKLM\SOFTWARE, so they cannot fake it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceStatusRegistry
{
    public static bool IsServiceInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceModeLayout.ServiceRegistryKey, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static ServicePublicStatus? TryRead()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceModeLayout.PublicStatusKey, writable: false);
            if (key?.GetValue("State") is not string state)
            {
                return null;
            }

            uint? session = key.GetValue("ConsoleSession") is int s && s >= 0 ? (uint)s : null;
            string? deviceId = key.GetValue("DeviceId") is string id && DeviceIds.TryNormalize(id, out var n) ? n : null;
            bool unattended = key.GetValue("UnattendedReady") is int u && u == 1;
            string? desktop = key.GetValue("InputDesktop") as string;
            var updated = key.GetValue("Updated") is string t
                && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                    ? when
                    : DateTimeOffset.MinValue;
            return new ServicePublicStatus(state, session, deviceId, unattended, string.IsNullOrEmpty(desktop) ? null : desktop, updated);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Service only (needs write access to HKLM).</summary>
    public static void Write(ServicePublicStatus status)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ServiceModeLayout.PublicStatusKey, writable: true);
        key.SetValue("State", status.State, RegistryValueKind.String);
        key.SetValue("ConsoleSession", status.ConsoleSessionId is uint s ? (int)s : -1, RegistryValueKind.DWord);
        key.SetValue("DeviceId", status.DeviceId ?? string.Empty, RegistryValueKind.String);
        key.SetValue("UnattendedReady", status.UnattendedReady ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("InputDesktop", status.InputDesktop ?? string.Empty, RegistryValueKind.String);
        key.SetValue("Updated", status.Updated.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
    }
}
