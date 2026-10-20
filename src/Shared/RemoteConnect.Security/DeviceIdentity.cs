using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteConnect.Security;

/// <summary>
/// Per-installation cryptographic identity: an RSA-3072 key pair wrapped in a self-signed X.509 certificate.
/// The certificate is presented in mutual TLS; peers pin <see cref="Fingerprint"/>.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private const string PfxPassword = "remoteconnect-local"; // container password only; the file itself is protected at rest.

    private DeviceIdentity(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException("Device identity certificate must contain a private key.", nameof(certificate));
        }

        Certificate = certificate;
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        Fingerprint = CertificateFingerprint.Compute(spki);
        DeviceId = DeviceIds.FromPublicKey(spki);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>9-digit address derived from the public key.</summary>
    public string DeviceId { get; }

    /// <summary>Hex SHA-256 of the SubjectPublicKeyInfo.</summary>
    public string Fingerprint { get; }

    public static DeviceIdentity Create()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=RemoteConnect Device"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1"), // server authentication
                new Oid("1.3.6.1.5.5.7.3.2"), // client authentication
            },
            false));

        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));

        // Round-trip through PKCS#12: SChannel (Windows) and SecureTransport (macOS) cannot use ephemeral keys for TLS.
        return FromPkcs12(ephemeral.Export(X509ContentType.Pkcs12, PfxPassword));
    }

    public static DeviceIdentity FromPkcs12(byte[] pkcs12)
    {
#pragma warning disable SYSLIB0057 // X509Certificate2 PKCS#12 constructor; X509CertificateLoader is .NET 9+.
        var cert = new X509Certificate2(pkcs12, PfxPassword, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
        return new DeviceIdentity(cert);
    }

    public byte[] ExportPkcs12() => Certificate.Export(X509ContentType.Pkcs12, PfxPassword);

    /// <summary>Public certificate only (DER), safe to publish to the signaling server.</summary>
    public byte[] ExportPublicCertificate() => Certificate.Export(X509ContentType.Cert);

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        using var rsa = Certificate.GetRSAPrivateKey()
            ?? throw new CryptographicException("Device identity has no RSA private key.");
        return rsa.SignData(data.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public void Dispose() => Certificate.Dispose();
}
