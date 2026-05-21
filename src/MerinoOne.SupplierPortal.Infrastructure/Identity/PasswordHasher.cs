using System.Security.Cryptography;
using System.Text;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

/// <summary>PBKDF2-HMAC-SHA256, 100K iterations, 16-byte salt. Stored as base64(salt).base64(hash).</summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return Hash(password, salt);
    }

    public static string Hash(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(HashSize);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var actual = pbkdf2.GetBytes(HashSize);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Used by seeders so the same password always hashes to the same string — re-runnable seed.</summary>
    public static string DeterministicHash(string password)
    {
        var salt = Encoding.UTF8.GetBytes("merino-supplier-portal-seed-salt");
        return Hash(password, salt.Take(SaltSize).ToArray());
    }
}
