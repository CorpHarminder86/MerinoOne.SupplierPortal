using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// R9 — PO-negotiation input document. Mirrors <see cref="Infor.PoNegotiationOutboundPayloadBuilder"/>
/// (header + changed lines with original snapshot + negotiated values). NOTE: PoNegotiationApprove outbox
/// rows carry EntityName=PurchaseOrder but EntityId = the NEGOTIATION id — this builder is resolved from
/// the config row's portalEntity, never from OutboxMessage.EntityName.
/// </summary>
public sealed class PoNegotiationInputDocumentBuilder : ILnInputDocumentBuilder
{
    public string PortalEntity => LnPortalEntity.PoNegotiation;
    public string BuilderVersion => LnInputDocumentVersions.PoNegotiation;

    public async Task<string?> BuildJsonAsync(IAppDbContext db, Guid entityId, string transactionType, string? outboxPayloadJson, CancellationToken ct = default)
    {
        var negotiation = await db.PurchaseOrderNegotiations
            .IgnoreQueryFilters()
            .Include(n => n.Lines)
            .FirstOrDefaultAsync(n => n.Id == entityId && !n.IsDeleted, ct);
        if (negotiation is null) return null;

        var doc = new PoNegotiationInputDoc(
            Id: negotiation.Id,
            PoNumber: negotiation.PoNumber,
            NegotiationId: negotiation.Id,
            SubmittedAt: negotiation.SubmittedAt.ToString("o"),
            NegotiationStatus: negotiation.NegotiationStatus.ToString(),
            Lines: negotiation.Lines
                .Where(l => !l.IsDeleted)
                .OrderBy(l => l.PositionNo).ThenBy(l => l.SequenceNo)
                .Select(l => new PoNegotiationLineInputDoc(
                    PositionNo: l.PositionNo,
                    SequenceNo: l.SequenceNo,
                    ItemCode: l.ItemCode,
                    OriginalQty: l.OriginalQty,
                    NegotiatedQty: l.NegotiatedQty,
                    OriginalDeliveryDate: l.OriginalDeliveryDate?.ToString("o"),
                    NegotiatedDeliveryDate: l.NegotiatedDeliveryDate?.ToString("o"),
                    OriginalPrice: l.OriginalPrice,
                    NegotiatedPrice: l.NegotiatedPrice))
                .ToList());

        return LnJson.SerializeInputDoc(doc);
    }
}
