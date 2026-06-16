using System.Security.Cryptography;
using System.Text;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

/// <summary>
/// One-way hashing for inbound X-APIKey credentials. API keys are high-entropy random secrets
/// (mok_ + 32 random bytes), so a plain SHA-256 hex digest is sufficient — no per-key salt or
/// slow KDF needed (unlike user passwords, which are low-entropy). Only the hex digest and a
/// short non-secret prefix are stored; the plaintext is shown once at creation.
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>SHA-256 hex digest (lowercase, 64 chars) of the full plaintext key.</summary>
    public static string Hash(string plaintextKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Constant-time comparison of a presented key against a stored hex digest. Uses
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> so verification time does not leak how
    /// many leading characters matched.
    /// </summary>
    public static bool Verify(string presentedKey, string storedHashHex)
    {
        if (string.IsNullOrEmpty(storedHashHex)) return false;
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));

        byte[] storedBytes;
        try { storedBytes = Convert.FromHexString(storedHashHex); }
        catch (FormatException) { return false; }

        // FixedTimeEquals already short-circuits to false on a length mismatch in constant time.
        return CryptographicOperations.FixedTimeEquals(presentedHash, storedBytes);
    }
}
