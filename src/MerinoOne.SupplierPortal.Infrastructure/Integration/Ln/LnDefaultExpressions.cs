using System.Text;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.1, mirrors R8's <see cref="Idm.IdmDefaultExpressions"/>) — the repo-versioned LN
/// expression catalogue, loaded from embedded <c>.jsonata</c> resources: per transaction type a
/// request/response/ack triple plus normalised SHA-256 hashes for the seeder hash-gate and the UI
/// drift flag. Also carries the shared error-extraction default (D-R9-5) and the two validation
/// sample documents (OData created-entity, erp-ack body).
/// </summary>
public sealed class LnDefaultExpressions : ILnExpressionCatalog
{
    public sealed record Entry(
        string TransactionType,
        string PortalEntity,
        string RequestExpr,
        string ResponseExpr,
        string AckExpr,
        string RequestHash,
        string ResponseHash,
        string AckHash);

    /// <summary>transactionType → portalEntity (the config/seed routing map — all 8 types, TSD correction #6).</summary>
    public static readonly IReadOnlyDictionary<string, string> PortalEntityByTransactionType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OutboxTransactionType.InvoicePost] = LnPortalEntity.Invoice,
            [OutboxTransactionType.AsnPost] = LnPortalEntity.Asn,
            [OutboxTransactionType.PoAcknowledge] = LnPortalEntity.PurchaseOrder,
            [OutboxTransactionType.PoAccept] = LnPortalEntity.PurchaseOrder,
            [OutboxTransactionType.PoReject] = LnPortalEntity.PurchaseOrder,
            [OutboxTransactionType.SupplierChange] = LnPortalEntity.SupplierChange,
            [OutboxTransactionType.SupplierSync] = LnPortalEntity.Supplier,
            [OutboxTransactionType.PoNegotiationApprove] = LnPortalEntity.PoNegotiation,
        };

    private readonly Dictionary<string, Entry> _byType = new(StringComparer.Ordinal);

    public LnDefaultExpressions()
    {
        foreach (var (type, portalEntity) in PortalEntityByTransactionType)
        {
            var request = Read($"{type}.request.jsonata");
            var response = Read($"{type}.response.jsonata");
            var ack = Read($"{type}.ack.jsonata");
            _byType[type] = new Entry(type, portalEntity, request, response, ack,
                ExpressionHash.Compute(request), ExpressionHash.Compute(response), ExpressionHash.Compute(ack));
        }
        ErrorMessageExpression = Read("LnErrorMessage.default.jsonata");
        ODataCreatedEntitySample = Read("ODataCreatedEntity.sample.json");
        ErpAckBodySample = Read("ErpAckBody.sample.json");
    }

    public Entry? TryGet(string transactionType) => _byType.TryGetValue(transactionType, out var e) ? e : null;

    public IReadOnlyCollection<Entry> All => _byType.Values;

    /// <summary>Shared default error-text extraction over a failed LN response body (D-R9-5 — text only, never class).</summary>
    public string ErrorMessageExpression { get; }

    /// <summary>Seeded onto <c>OutboundIntegrationConfig.responseSampleJson</c> — generic OData created-entity body.</summary>
    public string ODataCreatedEntitySample { get; }

    /// <summary>Seeded onto <c>OutboundIntegrationConfig.ackSampleJson</c> — one ErpAckRecord as pushed to /inbound/erp-ack.</summary>
    public string ErpAckBodySample { get; }

    // ── ILnExpressionCatalog (Application-facing view) ─────────────────────────────────────────────
    LnExpressionDefault? ILnExpressionCatalog.TryGet(string transactionType)
        => _byType.TryGetValue(transactionType, out var e)
            ? new LnExpressionDefault(e.TransactionType, e.PortalEntity, e.RequestExpr, e.ResponseExpr, e.AckExpr,
                e.RequestHash, e.ResponseHash, e.AckHash)
            : null;

    IReadOnlyCollection<LnExpressionDefault> ILnExpressionCatalog.All
        => _byType.Values
            .Select(e => new LnExpressionDefault(e.TransactionType, e.PortalEntity, e.RequestExpr, e.ResponseExpr,
                e.AckExpr, e.RequestHash, e.ResponseHash, e.AckHash))
            .ToList();

    string ILnExpressionCatalog.Hash(string expression) => ExpressionHash.Compute(expression);

    private static string Read(string fileName)
    {
        var asm = typeof(LnDefaultExpressions).Assembly;
        // Robust to root-namespace quirks: match the manifest name ending with the file name, scoped to the Ln folder.
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Contains(".Ln.", StringComparison.Ordinal) && n.EndsWith("." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded LN expression resource '{fileName}' not found.");
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
