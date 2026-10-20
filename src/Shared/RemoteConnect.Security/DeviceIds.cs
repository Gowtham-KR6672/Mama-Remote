using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RemoteConnect.Security;

/// <summary>
/// Human-friendly 9-digit device IDs (AnyDesk style, e.g. "482 913 207").
/// The ID is derived from the device's public key so it cannot be chosen freely, but it is only an
/// address: authentication always uses the full key fingerprint (see <see cref="CertificateFingerprint"/>).
/// </summary>
public static class DeviceIds
{
    public const int Length = 9;

    public static string FromPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(subjectPublicKeyInfo, hash);
        return FromHash(hash);
    }

    /// <summary>
    /// The fingerprint is SHA-256(SPKI) — the same hash the ID is derived from — so any announced
    /// (ID, fingerprint) pair can be checked for consistency without the certificate.
    /// </summary>
    public static bool TryFromFingerprint(string? fingerprintHex, out string deviceId)
    {
        deviceId = string.Empty;
        if (fingerprintHex is null || fingerprintHex.Length != 64)
        {
            return false;
        }

        try
        {
            deviceId = FromHash(Convert.FromHexString(fingerprintHex));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string FromHash(ReadOnlySpan<byte> hash)
    {
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(hash);
        // 100000000..999999999 so the ID never has a leading zero.
        ulong id = (value % 900_000_000UL) + 100_000_000UL;
        return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Formats "482913207" as "482 913 207".</summary>
    public static string Format(string id)
    {
        if (!TryNormalize(id, out var n))
        {
            return id;
        }

        return $"{n[..3]} {n[3..6]} {n[6..]}";
    }

    /// <summary>Accepts "482 913 207", "482-913-207" or "482913207".</summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var sb = new StringBuilder(Length);
        foreach (var c in input)
        {
            if (c is ' ' or '-' or '\t')
            {
                continue;
            }

            if (c is < '0' or > '9')
            {
                return false;
            }

            sb.Append(c);
        }

        if (sb.Length != Length || sb[0] == '0')
        {
            return false;
        }

        normalized = sb.ToString();
        return true;
    }
}
