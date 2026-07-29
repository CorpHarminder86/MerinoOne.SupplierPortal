using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// PurchaseOrder input document, shared by <c>PoAccept</c> and <c>PoReject</c> (R12/D14 retired the
/// PoAcknowledge outbound push). Folds the per-row parameters historically carried on
/// <c>OutboxMessage.PayloadJson</c> (<c>proposedDate</c>, <c>reason</c> — parsed with the same lenient rules
/// as the legacy dispatcher) into <c>responseContext</c> so gate and request mapping read one document
/// root (D-R9-7).
///
/// <para><b>R12 (purchaseOrder-v2)</b> — adds <c>companyCode</c>, the full <c>lines</c> set and the
/// precomputed <c>headerDeliveryDate</c> for the LN <c>PO_Update</c> contract. See
/// <see cref="PoLineDocumentAssembler"/> for why the line filter and the header-date decision are left to
/// the expression (D24) while the agree/disagree test is computed here.</para>
/// </summary>
public sealed class PurchaseOrderInputDocumentBuilder : ILnInputDocumentBuilder
{
    public string PortalEntity => LnPortalEntity.PurchaseOrder;
    public string BuilderVersion => LnInputDocumentVersions.PurchaseOrder;

    public async Task<string?> BuildJsonAsync(IAppDbContext db, Guid entityId, string transactionType, string? outboxPayloadJson, CancellationToken ct = default)
    {
        var po = await db.PurchaseOrders
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == entityId && !p.IsDeleted, ct);
        if (po is null) return null;

        var action = transactionType switch
        {
            OutboxTransactionType.PoAccept => "Accept",
            OutboxTransactionType.PoReject => "Reject",
            _ => transactionType,
        };

        var lines = await PoLineDocumentAssembler.LinesAsync(db, po.Id, dateOverrides: null, ct);

        var doc = new PurchaseOrderInputDoc(
            Id: po.Id,
            PoNumber: po.PoNumber,
            PoStatus: po.PoStatus.ToString(),
            ErpStatus: po.ErpStatus,
            AcknowledgmentAt: po.AcknowledgmentAt?.ToString("o"),
            AcceptedAt: po.AcceptedAt?.ToString("o"),
            RejectionReason: po.RejectionReason,
            CompanyCode: await PoLineDocumentAssembler.CompanyCodeAsync(db, po.TenantEntityId, ct),
            HeaderDeliveryDate: PoLineDocumentAssembler.HeaderDeliveryDate(lines),
            Lines: lines,
            ResponseContext: new PoResponseContextInputDoc(
                Action: action,
                ProposedDeliveryDate: ParseProposedDate(outboxPayloadJson)?.ToString("o"),
                Reason: ParseReason(outboxPayloadJson)));

        return LnJson.SerializeInputDoc(doc);
    }

    // NOTE the two date conventions in this document, deliberately: the AUDIT fields (acknowledgmentAt,
    // acceptedAt, responseContext.proposedDeliveryDate) keep "o" — nothing reads them on the wire and the
    // gate/editor tokens have always shown them that way. Only the fields that reach LN
    // (headerDeliveryDate, lines[].deliveryDate) use the D4 seconds-precision "Z" format.

    // Lenient parse rules copied from OutboxDispatcherWorker (malformed payload → absent value).
    private static DateTime? ParseProposedDate(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("proposedDate", out var el) &&
                el.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(el.GetString(), out var dt))
                return dt;
        }
        catch { /* malformed payload → no proposed date */ }
        return null;
    }

    private static string? ParseReason(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("reason", out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch { /* malformed payload → no reason */ }
        return null;
    }
}
