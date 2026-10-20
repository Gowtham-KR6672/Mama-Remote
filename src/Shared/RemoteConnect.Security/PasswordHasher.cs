using System.Globalization;
using System.Security.Cryptography;

namespace RemoteConnect.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 hashing for the unattended-access password. Format:
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c>.
/// </summary>
public static class PasswordHasher
{
    public const int MinimumLength = 8;
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 310_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        ValidateStrength(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string? password, string? encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < 100_000 || iterations > 5_000_000)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (password.Length > 1024)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static void ValidateStrength(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
        {
            throw new ArgumentException($"Password must be at least {MinimumLength} characters.", nameof(password));
        }

        if (password.Length > 1024)
        {
            throw new ArgumentException("Password is too long.", nameof(password));
        }
    }
}
