using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RemoteConnect.Security;

/// <summary>
/// Stores small secrets at rest. Windows: DPAPI (CurrentUser scope). macOS/Linux: file mode 0600 inside the
/// per-user application-support directory (Keychain integration is planned, see docs/SECURITY.md).
/// </summary>
public static class ProtectedFileStore
{
    private static readonly byte[] Entropy = "RemoteConnect.ProtectedFileStore.v1"u8.ToArray();

    public static void Write(string path, byte[] secret)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = OperatingSystem.IsWindows() ? ProtectWindows(secret) : secret;
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, payload);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static byte[]? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var payload = File.ReadAllBytes(path);
        try
        {
            return OperatingSystem.IsWindows() ? UnprotectWindows(payload) : payload;
        }
        catch (CryptographicException)
        {
            // Copied from another user/machine or corrupted: caller regenerates.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data)
        => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data)
        => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
