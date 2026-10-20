using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace RemoteConnect.Security;

/// <summary>
/// TLS configuration for peer-to-peer sessions. Both sides present their device certificate (mutual TLS).
/// Certificates are self-signed device identities, so trust is established by pinning the SPKI fingerprint
/// (from the signaling server or trust-on-first-use) rather than by a CA chain.
/// </summary>
public static class TlsSettings
{
    public static SslProtocols Protocols
        // SecureTransport on macOS does not offer TLS 1.3 to .NET 8; TLS 1.2 is the floor everywhere.
        => OperatingSystem.IsMacOS() ? SslProtocols.Tls12 : SslProtocols.Tls12 | SslProtocols.Tls13;

    public static SslServerAuthenticationOptions Server(DeviceIdentity identity, Action<X509Certificate2?> onClientCertificate)
        => new()
        {
            ServerCertificate = identity.Certificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = Protocols,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                // A client certificate is mandatory; its identity is checked against the claimed device ID afterwards.
                if (cert is null)
                {
                    onClientCertificate(null);
                    return false;
                }

                onClientCertificate(new X509Certificate2(cert));
                return true;
            },
        };

    /// <param name="expectedFingerprint">Pinned host fingerprint, or null for trust-on-first-use (caller must confirm).</param>
    public static SslClientAuthenticationOptions Client(
        DeviceIdentity identity,
        string? expectedFingerprint,
        Action<X509Certificate2?> onServerCertificate)
        => new()
        {
            TargetHost = "remoteconnect-device",
            ClientCertificates = new X509CertificateCollection { identity.Certificate },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.Certificate,
            EnabledSslProtocols = Protocols,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null)
                {
                    onServerCertificate(null);
                    return false;
                }

                var x509 = new X509Certificate2(cert);
                onServerCertificate(x509);
                return expectedFingerprint is null
                    || CertificateFingerprint.AreEqual(expectedFingerprint, CertificateFingerprint.Compute(x509));
            },
        };
}
