using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// R9 — Invoice input document. Mirrors <see cref="Infor.InvoiceOutboundPayloadBuilder"/>'s loading
/// (IgnoreQueryFilters + soft-delete guard) and adds the gate-facing superset: status, ERP composite key,
/// supplier codes, and the cross-entity GRN-coverage summary (the gate's only window onto GoodsReceipt
/// state — TSD §2.5a keeps the GRN condition in the gate, never the candidate filter).
/// </summary>
public sealed class InvoiceInputDocumentBuilder : ILnInputDocumentBuilder
{
    public string PortalEntity => LnPortalEntity.Invoice;
    public string BuilderVersion => LnInputDocumentVersions.Invoice;

    public async Task<string?> BuildJsonAsync(IAppDbContext db, Guid entityId, string transactionType, string? outboxPayloadJson, CancellationToken ct = default)
    {
        var invoice = await db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == entityId && !i.IsDeleted, ct);
        if (invoice is null) return null;

        var supplier = await db.Suppliers
            .IgnoreQueryFilters()
            .Where(s => s.Id == invoice.SupplierId && !s.IsDeleted)
            .Select(s => new { s.SupplierCode, s.ErpCode })
            .FirstOrDefaultAsync(ct);

        // Same predicate family as UpsertGoodsReceiptStatusCommand.AllCoveringGrnsApprovedAsync, DB-side only.
        // At enqueue time the caller injects in-flight coverage via LnInputDocOverrides (Phase B) — this
        // builder reports committed state, which is authoritative at dispatch/sweep/backfill time.
        var grnStatuses = await db.GoodsReceipts
            .IgnoreQueryFilters()
            .Where(g => g.InvoiceId == invoice.Id && !g.IsDeleted)
            .Select(g => g.GrnStatus)
            .ToListAsync(ct);

        var doc = new InvoiceInputDoc(
            Id: invoice.Id,
            InvoiceNumber: invoice.InvoiceNumber,
            InvoiceDate: invoice.InvoiceDate.ToString("o"),
            Currency: invoice.CurrencyCode,
            InvoiceAmount: invoice.InvoiceAmount,
            TaxAmount: invoice.TaxAmount,
            NetAmount: invoice.NetAmount,
            EInvoiceIrn: invoice.EInvoiceIrn,
            InvoiceStatus: invoice.InvoiceStatus.ToString(),
            GrnReference: invoice.GrnReference,
            ErpCode: invoice.ErpCode,
            ErpCompany: invoice.ErpCompany,
            ErpTransactionType: invoice.ErpTransactionType,
            ErpDocumentNo: invoice.ErpDocumentNo,
            ErpPostInitiatedAt: invoice.ErpPostInitiatedAt?.ToString("o"),
            ErpPostedAt: invoice.ErpPostedAt?.ToString("o"),
            SupplierId: invoice.SupplierId,
            SupplierCode: supplier?.SupplierCode,
            SupplierErpCode: supplier?.ErpCode,
            HasCoveringGrns: grnStatuses.Count > 0,
            AllCoveringGrnsApproved: grnStatuses.Count > 0 && grnStatuses.All(s => s == GrnStatus.GrnApproved));

        return LnJson.SerializeInputDoc(doc);
    }
}
