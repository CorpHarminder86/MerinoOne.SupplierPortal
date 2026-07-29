using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// PO-negotiation input document. NOTE: PoNegotiationApprove outbox rows carry EntityName=PurchaseOrder but
/// EntityId = the NEGOTIATION id — this builder is resolved from the config row's portalEntity, never from
/// <c>OutboxMessage.EntityName</c>.
///
/// <para><b>R12 (poNegotiation-v2)</b> — the document now leads with the PO_Update payload material
/// (<c>companyCode</c>, <c>lines</c>, <c>headerDeliveryDate</c>) in the SAME shape as
/// <see cref="PurchaseOrderInputDocumentBuilder"/>, so all three request expressions are identical text bar
/// the <c>POStatus</c> literal. The negotiation delta rows moved to <c>negotiationLines</c>.</para>
///
/// <para><b>D10</b> — the approved negotiation's per-line <c>negotiatedDeliveryDate</c> is overlaid onto the
/// PO's own line set; lines the negotiation never touched keep their PO date. This overlay exists precisely
/// because <c>ApprovePoNegotiationCommand</c> deliberately does NOT mutate <c>PurchaseOrderLine</c> (ERP
/// re-syncs the revised PO inbound), so the agreed dates live nowhere else.</para>
///
/// <para><b>Company comes from the PO, not the negotiation</b> — the same source
/// <c>LnDocumentCompanyResolver</c> uses for the <c>X-Infor-LnCompany</c> header, so the body company and the
/// header can never disagree.</para>
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

        // The PO is required for the company code and the line set. A negotiation whose PO is gone cannot
        // produce a payload — return null so the dispatcher treats it as "entity not found" (retriable),
        // exactly as it does for a missing root.
        var po = await db.PurchaseOrders
            .IgnoreQueryFilters()
            .Where(p => p.Id == negotiation.PurchaseOrderId && !p.IsDeleted)
            .Select(p => new { p.Id, p.TenantEntityId })
            .FirstOrDefaultAsync(ct);
        if (po is null) return null;

        var negotiationLines = negotiation.Lines
            .Where(l => !l.IsDeleted)
            .OrderBy(l => l.PositionNo).ThenBy(l => l.SequenceNo)
            .ToList();

        // D10 overlay. Last-wins on a duplicate (positionNo, sequenceNo) rather than throwing: a malformed
        // negotiation must not be able to kill the dispatch loop.
        var overrides = new Dictionary<(int, int), DateTime?>();
        foreach (var l in negotiationLines)
            overrides[(l.PositionNo, l.SequenceNo)] = l.NegotiatedDeliveryDate;

        var lines = await PoLineDocumentAssembler.LinesAsync(db, po.Id, overrides, ct);

        var doc = new PoNegotiationInputDoc(
            Id: negotiation.Id,
            PoNumber: negotiation.PoNumber,
            NegotiationId: negotiation.Id,
            SubmittedAt: negotiation.SubmittedAt.ToString("o"),
            NegotiationStatus: negotiation.NegotiationStatus.ToString(),
            CompanyCode: await PoLineDocumentAssembler.CompanyCodeAsync(db, po.TenantEntityId, ct),
            HeaderDeliveryDate: PoLineDocumentAssembler.HeaderDeliveryDate(lines),
            Lines: lines,
            NegotiationLines: negotiationLines
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
