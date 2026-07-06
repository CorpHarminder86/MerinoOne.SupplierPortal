using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// R9 — PurchaseOrder input document, shared by PoAcknowledge / PoAccept / PoReject. Folds the per-row
/// parameters historically carried on <c>OutboxMessage.PayloadJson</c> (<c>proposedDate</c>, <c>reason</c> —
/// parsed with the same lenient rules as the legacy dispatcher) into <c>responseContext</c> so gate and
/// request mapping read one document root (D-R9-7). <c>proposedDeliveryDate</c> is re-formatted "o" to match
/// the legacy Accept payload byte-for-byte.
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
            OutboxTransactionType.PoAcknowledge => "Acknowledge",
            OutboxTransactionType.PoAccept => "Accept",
            OutboxTransactionType.PoReject => "Reject",
            _ => transactionType,
        };

        var doc = new PurchaseOrderInputDoc(
            Id: po.Id,
            PoNumber: po.PoNumber,
            PoStatus: po.PoStatus.ToString(),
            ErpStatus: po.ErpStatus,
            AcknowledgmentAt: po.AcknowledgmentAt?.ToString("o"),
            AcceptedAt: po.AcceptedAt?.ToString("o"),
            RejectionReason: po.RejectionReason,
            ResponseContext: new PoResponseContextInputDoc(
                Action: action,
                ProposedDeliveryDate: ParseProposedDate(outboxPayloadJson)?.ToString("o"),
                Reason: ParseReason(outboxPayloadJson)));

        return LnJson.SerializeInputDoc(doc);
    }

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
