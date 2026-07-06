using System.Security.Cryptography;
using System.Text;

namespace MerinoOne.SupplierPortal.Application.Common.Integration;

/// <summary>
/// R9 (extracted from R8's <c>IdmDefaultExpressions.Hash</c>) — the ONE normalised expression hash used by
/// every repo-expression catalogue, seeder hash-gate, and compiled-expression cache key. Line endings are
/// folded to LF and the text trimmed before hashing so a CRLF/LF checkout difference never reads as drift
/// and never causes a duplicate cache entry.
/// </summary>
public static class ExpressionHash
{
    /// <summary>Normalised SHA-256 (hex, lower-case): CRLF/CR → LF, trimmed.</summary>
    public static string Compute(string? text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
