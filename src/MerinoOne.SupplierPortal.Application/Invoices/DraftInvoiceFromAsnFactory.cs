using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices;

/// <summary>
/// R4 (2026-06-22) — Module 4. Single source of truth for creating the ONE draft <see cref="Invoice"/> that
/// spans ALL of an ASN's POs (Q1b). Used by <c>SubmitAsnCommand</c> (auto, in the submit transaction) and by
/// <c>CreateInvoiceFromAsnCommand</c> (manual). Guards <c>UQ_Invoice_asnId</c> with an upsert-or-skip: if a
/// (non-deleted) invoice already exists for the ASN it is returned, never a blind second insert.
///
/// The created invoice's <c>PurchaseOrderId</c> is the scalar PO when the ASN covers a single PO, else null
/// (PO context lives per-line on <c>InvoiceLine.purchaseOrderLineId</c>). Lines are copied from the ASN lines
/// across all covered POs; <c>BilledQty</c> is capped at the PO line's ordered qty (over-ship → cap, per the
/// plan default). Adds the entity to the supplied change tracker but does NOT SaveChanges — the caller commits.
/// </summary>
public sealed class DraftInvoiceFromAsnFactory
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public DraftInvoiceFromAsnFactory(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public sealed record Result(Invoice Invoice, bool Created);

    /// <summary>
    /// Builds (or returns the existing) draft invoice for <paramref name="asn"/>. Validates the mixed-currency
    /// guard (R16) — all covered POs must share one currency — and throws <see cref="ValidationException"/> if not.
    /// </summary>
    public async Task<Result> EnsureDraftAsync(Asn asn, DateTime now, CancellationToken ct)
    {
        // UQ_Invoice_asnId upsert-or-skip: an invoice already on this ASN? Return it, never double-insert.
        var existing = await _db.Invoices.FirstOrDefaultAsync(i => i.AsnId == asn.Id && !i.IsDeleted, ct);
        if (existing is not null)
            return new Result(existing, Created: false);

        // Covered POs = junction rows ∪ legacy scalar header PO.
        var coveredPoIds = await _db.AsnPurchaseOrders
            .Where(j => j.AsnId == asn.Id && !j.IsDeleted)
            .Select(j => j.PurchaseOrderId)
            .ToListAsync(ct);
        var poSet = coveredPoIds.ToHashSet();
        if (asn.PurchaseOrderId.HasValue) poSet.Add(asn.PurchaseOrderId.Value);

        var pos = await _db.PurchaseOrders.Where(p => poSet.Contains(p.Id)).ToListAsync(ct);

        // R16 mixed-currency guard — Invoice.CurrencyCode is single-valued, so one invoice cannot span POs of
        // different currencies. Block submit with a clear message (treat null/blank currency as a sentinel).
        var currencies = pos.Select(p => string.IsNullOrWhiteSpace(p.CurrencyCode) ? "INR" : p.CurrencyCode!.Trim().ToUpperInvariant())
                            .Distinct().ToList();
        if (currencies.Count > 1)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["currencyCode"] = new[]
                {
                    $"All POs on this ASN must share one currency to create a single invoice; found: {string.Join(", ", currencies)}."
                }
            });
        var currencyCode = currencies.Count == 1 ? currencies[0] : "INR";

        // Pull the ASN lines joined to their PO lines (item/price context for the invoice lines).
        var sourceLines = await (from al in _db.AsnLines
                                 join pol in _db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                                 where al.AsnId == asn.Id
                                 select new
                                 {
                                     al.PurchaseOrderLineId,
                                     al.ShippedQty,
                                     pol.OrderQty,
                                     pol.ItemCode,
                                     pol.ItemDescription,
                                     pol.ItemId,
                                     pol.PriceUnit,
                                     pol.TaxCode,
                                 }).ToListAsync(ct);

        if (sourceLines.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "Cannot create an invoice from an ASN with no lines." }
            });

        var invoiceId = Guid.NewGuid();
        decimal netAmount = 0m;
        var lines = new List<InvoiceLine>();
        foreach (var sl in sourceLines)
        {
            // Over-ship cap: bill the lesser of shipped vs ordered (plan default — draft invoice qty caps at ordered).
            var billedQty = sl.OrderQty > 0 && sl.ShippedQty > sl.OrderQty ? sl.OrderQty : sl.ShippedQty;
            var lineAmount = decimal.Round(billedQty * sl.PriceUnit, 2);
            netAmount += lineAmount;
            lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoiceId,
                PurchaseOrderLineId = sl.PurchaseOrderLineId,
                ItemCode = sl.ItemCode,
                ItemDescription = sl.ItemDescription,
                ItemId = sl.ItemId,
                BilledQty = billedQty,
                UnitPrice = sl.PriceUnit,
                LineAmount = lineAmount,
                TaxCode = sl.TaxCode,
                TaxAmount = 0m,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            });
        }

        // Single-PO → set the scalar header FK for back-compat; multi-PO → null (PO context lives per-line).
        var distinctLinePoCount = pos.Count;
        Guid? headerPoId = poSet.Count == 1 ? poSet.First() : null;

        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceNumber = $"INV-DRAFT-{asn.AsnNumber}",   // placeholder; supplier sets the real number in Draft.
            PurchaseOrderId = headerPoId,
            AsnId = asn.Id,
            SupplierId = asn.SupplierId,
            InvoiceDate = now.Date,
            InvoiceAmount = netAmount,
            TaxAmount = 0m,
            NetAmount = netAmount,
            CurrencyCode = currencyCode,
            MatchingType = MatchingType.ThreeWay,
            InvoiceStatus = InvoiceStatus.Draft,
            SeccodeId = asn.SeccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = now,
        };
        foreach (var l in lines) invoice.Lines.Add(l);

        _db.Invoices.Add(invoice);
        return new Result(invoice, Created: true);
    }
}
