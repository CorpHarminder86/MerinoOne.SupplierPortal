using System.Text;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Idm;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §4.4 / D6. The repo-versioned JSONata mapping catalogue, loaded from embedded
/// <c>.jsonata</c> resources. Exposes each type's create/mutate expression text plus a normalised SHA-256 so the
/// seeder can idempotently write config rows and the UI can compute drift (row text hash ≠ repo default hash).
/// Line endings are normalised before hashing so a CRLF/LF checkout difference never reads as drift.
/// </summary>
public sealed class IdmDefaultExpressions : IIdmExpressionCatalog
{
    public sealed record Entry(string IdmEntityType, string CreateExpression, string MutateExpression, string CreateHash, string MutateHash);

    // idmEntityType → default (portal entity, attachmentType, gate) seed (out-of-the-box mapping for the demo
    // tenant). R9 (§2.11): the gate is now a JSONata boolean expression — the shared IdmGateConversion helper
    // renders the same required-non-null semantics the old dot-path arrays carried.
    public static readonly IReadOnlyDictionary<string, (string OwnerEntityType, string AttachmentType, string GateExpr)> Seeds =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["InforInvoice"] = ("Invoice", "Invoice",
                IdmGateConversion.ToJsonata(new[] { "invoice.erpCompany", "invoice.erpTransactionType", "invoice.erpDocumentNo" })),
            ["InforAdvanceShipmentNoticeSupplierASN"] = ("Asn", "AsnAttachment",
                IdmGateConversion.ToJsonata(new[] { "asn.erpCompany", "asn.erpTransactionType", "asn.erpDocumentNo" })),
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

    // IIdmExpressionCatalog (Application-facing view).
    IdmExpressionDefault? IIdmExpressionCatalog.TryGet(string idmEntityType)
        => _byType.TryGetValue(idmEntityType, out var e)
            ? new IdmExpressionDefault(e.IdmEntityType, e.CreateExpression, e.MutateExpression, e.CreateHash, e.MutateHash)
            : null;

    string IIdmExpressionCatalog.Hash(string expression) => Hash(expression);

    /// <summary>Normalised SHA-256 (hex, lower-case) of an expression — delegates to the shared <see cref="ExpressionHash"/> (R9 extraction).</summary>
    public static string Hash(string text) => ExpressionHash.Compute(text);

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
