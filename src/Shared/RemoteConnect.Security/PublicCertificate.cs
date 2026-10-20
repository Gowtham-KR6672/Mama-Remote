using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteConnect.Security;

/// <summary>Verification helpers for a peer's public certificate (no private key).</summary>
public static class PublicCertificate
{
    public const int MaxDerLength = 8 * 1024;

    public static X509Certificate2 Load(byte[] der)
    {
        if (der is null || der.Length == 0 || der.Length > MaxDerLength)
        {
            throw new CryptographicException("Certificate is missing or too large.");
        }

#pragma warning disable SYSLIB0057 // X509CertificateLoader is .NET 9+.
        return new X509Certificate2(der);
#pragma warning restore SYSLIB0057
    }

    public static string DeriveDeviceId(X509Certificate2 certificate)
        => DeviceIds.FromPublicKey(certificate.PublicKey.ExportSubjectPublicKeyInfo());

    public static bool VerifySignature(X509Certificate2 certificate, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize < 2048)
        {
            return false;
        }

        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>The exact bytes a device signs to prove key possession when registering with the server.</summary>
    public static byte[] RegistrationPayload(string deviceId, string challenge)
        => System.Text.Encoding.UTF8.GetBytes($"RemoteConnect-Register|v1|{deviceId}|{challenge}");
}
