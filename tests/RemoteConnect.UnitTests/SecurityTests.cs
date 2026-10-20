using RemoteConnect.Security;

namespace RemoteConnect.UnitTests;

public class SecurityTests
{
    private static readonly Lazy<DeviceIdentity> Identity = new(DeviceIdentity.Create);

    [Fact]
    public void DeviceId_is_nine_digits_derived_from_key_and_matches_fingerprint()
    {
        var id = Identity.Value;
        Assert.Matches("^[1-9][0-9]{8}$", id.DeviceId);
        Assert.Equal(id.DeviceId, PublicCertificate.DeriveDeviceId(id.Certificate));
        Assert.True(DeviceIds.TryFromFingerprint(id.Fingerprint, out var fromFingerprint));
        Assert.Equal(id.DeviceId, fromFingerprint);
    }

    [Fact]
    public void Identity_survives_pkcs12_round_trip()
    {
        using var restored = DeviceIdentity.FromPkcs12(Identity.Value.ExportPkcs12());
        Assert.Equal(Identity.Value.DeviceId, restored.DeviceId);
        Assert.Equal(Identity.Value.Fingerprint, restored.Fingerprint);
        Assert.True(restored.Certificate.HasPrivateKey);
    }

    [Theory]
    [InlineData("482 913 207", "482913207")]
    [InlineData("482-913-207", "482913207")]
    [InlineData(" 482913207 ", "482913207")]
    public void Normalize_accepts_common_formats(string input, string expected)
    {
        Assert.True(DeviceIds.TryNormalize(input, out var n));
        Assert.Equal(expected, n);
        Assert.Equal("482 913 207", DeviceIds.Format(n));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678")]
    [InlineData("1234567890")]
    [InlineData("012345678")]
    [InlineData("12a456789")]
    [InlineData("192.168.1.1")]
    public void Normalize_rejects_invalid(string input) => Assert.False(DeviceIds.TryNormalize(input, out _));

    [Fact]
    public void Inconsistent_fingerprint_does_not_yield_the_id()
    {
        var fp = Identity.Value.Fingerprint;
        var tampered = (fp[0] == 'A' ? 'B' : 'A') + fp[1..];
        Assert.True(DeviceIds.TryFromFingerprint(tampered, out var other));
        Assert.NotEqual(Identity.Value.DeviceId, other);
        Assert.False(DeviceIds.TryFromFingerprint("XYZ", out _));
    }

    [Fact]
    public void Registration_signature_verifies_only_for_the_signed_payload()
    {
        var id = Identity.Value;
        var payload = PublicCertificate.RegistrationPayload(id.DeviceId, "challenge-1");
        var sig = id.Sign(payload);
        using var pub = PublicCertificate.Load(id.ExportPublicCertificate());
        Assert.False(pub.HasPrivateKey);
        Assert.True(PublicCertificate.VerifySignature(pub, payload, sig));
        Assert.False(PublicCertificate.VerifySignature(pub, PublicCertificate.RegistrationPayload(id.DeviceId, "challenge-2"), sig));
    }

    [Fact]
    public void Password_hash_verifies_correct_password_only()
    {
        var hash = PasswordHasher.Hash("correct horse battery");
        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("Correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("", hash));
        Assert.False(PasswordHasher.Verify("correct horse battery", "garbage"));
        Assert.NotEqual(hash, PasswordHasher.Hash("correct horse battery")); // salted
    }

    [Fact]
    public void Short_passwords_are_rejected() => Assert.Throws<ArgumentException>(() => PasswordHasher.Hash("short"));

    [Fact]
    public void Fingerprint_comparison_is_case_insensitive()
    {
        var fp = Identity.Value.Fingerprint;
        Assert.True(CertificateFingerprint.AreEqual(fp, fp.ToLowerInvariant()));
        Assert.False(CertificateFingerprint.AreEqual(fp, fp[..^1]));
        Assert.False(CertificateFingerprint.AreEqual(fp, null));
        Assert.Equal(23, CertificateFingerprint.ToDisplay(fp).Length); // 8 bytes "AA:BB:..."
    }
}
