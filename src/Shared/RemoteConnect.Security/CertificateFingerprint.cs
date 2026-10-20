using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteConnect.Security;

/// <summary>
/// SHA-256 over the certificate's SubjectPublicKeyInfo. This is the value that is pinned and compared;
/// it survives certificate re-issuance with the same key.
/// </summary>
public static class CertificateFingerprint
{
    public static string Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Compute(certificate.PublicKey.ExportSubjectPublicKeyInfo());
    }

    public static string Compute(ReadOnlySpan<byte> subjectPublicKeyInfo)
        => Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo));

    /// <summary>Constant-time comparison of two hex fingerprints (case-insensitive).</summary>
    public static bool AreEqual(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        Span<byte> left = stackalloc byte[a.Length];
        Span<byte> right = stackalloc byte[b.Length];
        for (int i = 0; i < a.Length; i++)
        {
            left[i] = (byte)char.ToUpperInvariant(a[i]);
            right[i] = (byte)char.ToUpperInvariant(b[i]);
        }

        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>Short form for display, e.g. "3F:A2:91:0C:7B:44:E0:12".</summary>
    public static string ToDisplay(string fingerprint, int bytes = 8)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return string.Empty;
        }

        int chars = Math.Min(fingerprint.Length, bytes * 2);
        var parts = new List<string>(bytes);
        for (int i = 0; i + 1 < chars; i += 2)
        {
            parts.Add(fingerprint.Substring(i, 2));
        }

        return string.Join(':', parts);
    }
}
