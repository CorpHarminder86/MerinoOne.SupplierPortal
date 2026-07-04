using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §4.4 / D6. The repo-versioned JSONata mapping catalogue, loaded from embedded
/// <c>.jsonata</c> resources. Exposes each type's create/mutate expression text plus a normalised SHA-256 so the
/// seeder can idempotently write config rows and the UI can compute drift (row text hash ≠ repo default hash).
/// Line endings are normalised before hashing so a CRLF/LF checkout difference never reads as drift.
/// </summary>
public sealed class IdmDefaultExpressions
{
    public sealed record Entry(string IdmEntityType, string CreateExpression, string MutateExpression, string CreateHash, string MutateHash);

    // idmEntityType → default attachmentType seed (the out-of-the-box mapping for the demo/seed tenant).
    public static readonly IReadOnlyDictionary<string, (string AttachmentType, string GateJson)> Seeds =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["InforInvoice"] = ("Invoice",
                "[\"invoice.erpCompany\",\"invoice.erpTransactionType\",\"invoice.erpDocumentNo\"]"),
            ["InforAdvanceShipmentNoticeSupplierASN"] = ("AsnAttachment",
                "[\"asn.erpCompany\",\"asn.erpTransactionType\",\"asn.erpDocumentNo\"]"),
        };

    private readonly Dictionary<string, Entry> _byType = new(StringComparer.Ordinal);

    public IdmDefaultExpressions()
    {
        foreach (var type in Seeds.Keys)
        {
            var create = Read($"{type}.create.jsonata");
            var mutate = Read($"{type}.mutate.jsonata");
            _byType[type] = new Entry(type, create, mutate, Hash(create), Hash(mutate));
        }
    }

    public Entry? TryGet(string idmEntityType) => _byType.TryGetValue(idmEntityType, out var e) ? e : null;

    public IReadOnlyCollection<Entry> All => _byType.Values;

    /// <summary>Normalised SHA-256 (hex, lower-case) of an expression — line endings folded to LF + trimmed.</summary>
    public static string Hash(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Read(string fileName)
    {
        var asm = typeof(IdmDefaultExpressions).Assembly;
        // Robust to root-namespace quirks: match the manifest name ending with the file name.
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded IDM expression resource '{fileName}' not found.");
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
