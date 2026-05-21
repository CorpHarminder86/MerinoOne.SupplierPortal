using System.Security.Cryptography;
using System.Text;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>Stable name-based Guids so seeders are idempotent across machines and environments.</summary>
public static class DeterministicId
{
    public static Guid From(string ns, string key)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ns + "::" + key));
        // RFC 4122 v3-style — clear version and variant bits
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
