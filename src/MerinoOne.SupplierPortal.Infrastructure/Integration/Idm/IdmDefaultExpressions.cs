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
    // CAUTION (R10, 2026-07-07): only the KEYS still drive behaviour — they pick which .jsonata resources load.
    // IdmOutboundSeeder was deleted when Document rows folded into OutboundIntegrationConfig, so the tuple
    // values (portal entity / attachment type / gate) are DOCUMENTATION of the intended default: editing them
    // does not touch any existing config row. Changing a live gate means updating the row (UI or SQL).
    public static readonly IReadOnlyDictionary<string, (string OwnerEntityType, string AttachmentType, string GateExpr)> Seeds =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["InforInvoice"] = ("Invoice", "Invoice",
                IdmGateConversion.ToJsonata(new[] { "invoice.erpCompany", "invoice.erpTransactionType", "invoice.erpDocumentNo" })),
            // R11.2 (2026-07-29) — the ASN's erpCompany/erpTransactionType/erpDocumentNo columns were dropped;
            // the "LN has the record" signal is erpCode (the ASNNo written back by /inbound/erp-ack).
            // R16.1 (2026-08-11) — supplier.erpCode joins the gate: it is the fresh mapping's MDS_id1, so a
            // supplier without one must hold the document Blocked instead of pushing a blank key.
            // 2026-08-12 — renamed from "InforAdvanceShipmentNoticeSupplierASN"; the key must equal
            // AsnSnapshotProvider.IdmEntityType (it is also the .jsonata resource prefix loaded below).
            ["InforAdvanceShipmentNotice"] = ("Asn", "AsnAttachment",
                IdmGateConversion.ToJsonata(new[] { "asn.erpCode", "asn.supplier.erpCode" })),
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
