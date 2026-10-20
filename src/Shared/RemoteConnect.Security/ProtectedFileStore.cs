using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RemoteConnect.Security;

/// <summary>
/// Stores small secrets at rest. Windows: DPAPI (CurrentUser scope, or LocalMachine scope for the service-mode host,
/// whose files are additionally protected by the LocalSystem profile ACL). macOS/Linux: file mode 0600 inside the
/// per-user application-support directory (Keychain integration is planned, see docs/SECURITY.md).
/// </summary>
public static class ProtectedFileStore
{
    private static readonly byte[] Entropy = "RemoteConnect.ProtectedFileStore.v1"u8.ToArray();

    public static void Write(string path, byte[] secret, bool machineScope = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = OperatingSystem.IsWindows() ? ProtectWindows(secret, machineScope) : secret;
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, payload);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static byte[]? TryRead(string path, bool machineScope = false)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var payload = File.ReadAllBytes(path);
        try
        {
            return OperatingSystem.IsWindows() ? UnprotectWindows(payload, machineScope) : payload;
        }
        catch (CryptographicException)
        {
            // Copied from another user/machine or corrupted: caller regenerates.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data, bool machineScope)
        => ProtectedData.Protect(data, Entropy, machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data, bool machineScope)
        => ProtectedData.Unprotect(data, Entropy, machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
}
