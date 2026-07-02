using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices;

/// <summary>
/// R6 (2026-07-02) — grouped draft-invoice generation from a submitted ASN (supersedes the R4 one-invoice-per-ASN
/// model; <c>UQ_Invoice_asnId</c> became the non-unique <c>IX_Invoice_asnId</c> in migration 0042). Called from
/// <c>AsnSubmitExecutor</c> (inside the approve transaction) and <c>CreateInvoiceFromAsnCommand</c> (manual /
/// Retry). Stages entities on the supplied change tracker but does NOT SaveChanges — the caller commits.
///
/// <list type="bullet">
///   <item><b>Idempotent per ASN:</b> any existing non-deleted invoice for the ASN ⇒ all of them are returned,
///         no new rows.</item>
///   <item><b>Tax gate (whole-ASN block):</b> any included line whose PO-line <c>taxId</c> is null OR resolves to
///         a null rate ⇒ NO invoice is created; the ASN is flagged <c>InvoiceGenerationStatus="Blocked"</c> with a
///         note naming the tax code(s), and the mapped buyer users are notified (EmailOutbox, best-effort). This
///         path MUST NOT throw — the surrounding ASN approval still commits (plan D3).</item>
///   <item><b>Grouping (plan D4):</b> lines group by PO (currency, payment term) → ONE Draft invoice per group,
///         numbered <c>DRAFT-{asnNumber}-{groupSeq}</c> (plan D5). Per line
///         <c>BilledQty = max(0, shippedQtyToDate − invoicedQtyToDate)</c> read LIVE off the PO line (0-qty lines
///         skipped; an all-0 group is skipped; nothing at all to invoice ⇒ quiet no-op, status untouched).</item>
///   <item><b>Matching type:</b> ThreeWay iff EVERY line of the group already has a covering GRN for this ASN,
///         else TwoWay.</item>
///   <item>Tax snapshot per line (taxId/ratePct/code/description) is PROVISIONAL — submit re-resolves + freezes.</item>
/// </list>
/// </summary>
public sealed class DraftInvoiceFromAsnFactory
{
    public const string StatusGenerated = "Generated";
    public const string StatusBlocked = "Blocked";

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly TaxRateResolver _taxResolver;

    public DraftInvoiceFromAsnFactory(IAppDbContext db, ICurrentUser user, TaxRateResolver taxResolver)
    {
        _db = db; _user = user; _taxResolver = taxResolver;
    }

    /// <summary>
    /// <b>Created</b> = new drafts were staged this call. <b>Blocked</b> = the tax gate fired (no invoices, note
    /// set, ASN flagged). Existing-invoices and nothing-to-invoice both return Created=false, Blocked=false
    /// (distinguished by <see cref="Invoices"/> being non-empty vs empty).
    /// </summary>
    public sealed record Outcome(IReadOnlyList<Invoice> Invoices, bool Created, bool Blocked, string? BlockNote);

    public async Task<Outcome> EnsureDraftAsync(Asn asn, DateTime now, CancellationToken ct)
    {
        // Idempotency: any (non-deleted) invoice already generated/created for this ASN ⇒ return them all.
        var existing = await _db.Invoices
            .Where(i => i.AsnId == asn.Id && !i.IsDeleted)
            .OrderBy(i => i.CreatedOn).ThenBy(i => i.InvoiceNumber)
            .ToListAsync(ct);
        if (existing.Count > 0)
            return new Outcome(existing, Created: false, Blocked: false, BlockNote: null);

        // ASN lines joined to their PO lines + PO headers — price, tax and the grouping key (currency/payment
        // term). BilledQty reads the LIVE PO-line cumulatives, so lines are DEDUPED per PO line (two ASN lines on
        // one PO line must not double-bill the same remaining balance).
        var sourceLines = await (from al in _db.AsnLines
                                 join pol in _db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                                 join po in _db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                                 where al.AsnId == asn.Id && !al.IsDeleted
                                 select new
                                 {
                                     PoLineId = pol.Id,
                                     PoId = po.Id,
                                     po.CurrencyId,
                                     po.CurrencyCode,
                                     po.PaymentTermId,
                                     pol.ItemCode,
                                     pol.ItemDescription,
                                     pol.ItemId,
                                     pol.PriceUnit,
                                     pol.TaxCode,
                                     pol.TaxId,
                                     pol.ShippedQtyToDate,
                                     pol.InvoicedQtyToDate,
                                 }).ToListAsync(ct);
        var lines = sourceLines
            .GroupBy(l => l.PoLineId)
            .Select(g => g.First())
            .ToList();

        if (lines.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "Cannot create an invoice from an ASN with no lines." }
            });

        // ---- Tax gate (plan D12): every line needs a PO-line taxId that resolves to a NON-NULL rate ---------
        var resolved = await _taxResolver.ResolveAsync(
            lines.Where(l => l.TaxId.HasValue).Select(l => l.TaxId!.Value), asn.TenantId, ct);

        var offenders = lines
            .Where(l => l.TaxId is null
                        || !resolved.TryGetValue(l.TaxId.Value, out var t)
                        || t.Rate is null)
            .Select(l => l.TaxId.HasValue && resolved.TryGetValue(l.TaxId.Value, out var t)
                ? $"'{t.Code}' (no rate)"
                : !string.IsNullOrWhiteSpace(l.TaxCode)
                    ? $"'{l.TaxCode!.Trim()}' (not resolvable)"
                    : $"item '{l.ItemCode}' (no tax code)")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (offenders.Count > 0)
        {
            var note = Truncate(
                $"Invoice generation blocked — tax code(s) without a usable rate: {string.Join(", ", offenders)}. " +
                "Fix the tax master rate(s), then retry generation from the ASN.", 500);

            asn.InvoiceGenerationStatus = StatusBlocked;
            asn.InvoiceGenerationNote = note;

            // Best-effort buyer notification (mapped internal users, same resolution as the approval flow).
            var buyers = await AsnApprovalSupport.ResolveApproverUserIdsAsync(_db, asn, ct);
            await AsnApprovalSupport.NotifyBuyersInvoiceGenerationBlockedAsync(_db, asn, buyers.ToList(), note, now, ct);

            // NO invoice, NO throw — the ASN approval transaction continues and commits with the Blocked flag.
            return new Outcome(Array.Empty<Invoice>(), Created: false, Blocked: true, BlockNote: note);
        }

        // ---- GRN presence per line (drives the per-group matching type) ------------------------------------
        var poLineIds = lines.Select(l => l.PoLineId).ToList();
        var grnPoLineIds = (await _db.GoodsReceipts
                .Where(g => g.AsnId == asn.Id && !g.IsDeleted && poLineIds.Contains(g.PurchaseOrderLineId))
                .Select(g => g.PurchaseOrderLineId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        // ---- Group by PO (currency, payment term) — plan D4. The key folds the normalized CurrencyCode too,
        // because inbound POs routinely carry a code snapshot with a NULL CurrencyId (no Currency master row);
        // grouping on the FK alone would merge genuinely different currencies. ------------------------------
        var groups = lines
            .GroupBy(l => (l.CurrencyId, Code: NormalizeCurrency(l.CurrencyCode), l.PaymentTermId))
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .ThenBy(g => g.Key.PaymentTermId)
            .ThenBy(g => g.Key.CurrencyId)
            .ToList();

        var invoices = new List<Invoice>();
        var groupSeq = 0;
        foreach (var group in groups)
        {
            var billable = group
                .Select(l => new { Line = l, BilledQty = Math.Max(0m, l.ShippedQtyToDate - l.InvoicedQtyToDate) })
                .Where(x => x.BilledQty > 0)
                .ToList();
            if (billable.Count == 0) continue;   // fully invoiced group — nothing to draft.

            groupSeq++;
            var invoiceId = Guid.NewGuid();
            decimal invoiceAmount = 0m, taxAmount = 0m;
            var invoiceLines = new List<InvoiceLine>();
            foreach (var x in billable)
            {
                var tax = resolved[x.Line.TaxId!.Value];
                var lineAmount = decimal.Round(x.BilledQty * x.Line.PriceUnit, 2);
                var lineTax = decimal.Round(lineAmount * tax.Rate!.Value / 100m, 2);
                invoiceAmount += lineAmount;
                taxAmount += lineTax;
                invoiceLines.Add(new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    PurchaseOrderLineId = x.Line.PoLineId,
                    ItemCode = x.Line.ItemCode,
                    ItemDescription = x.Line.ItemDescription,
                    ItemId = x.Line.ItemId,
                    BilledQty = x.BilledQty,
                    UnitPrice = x.Line.PriceUnit,
                    LineAmount = lineAmount,
                    TaxCode = tax.Code,
                    TaxDescription = tax.Description,
                    TaxId = x.Line.TaxId,
                    TaxRatePct = tax.Rate,
                    TaxAmount = lineTax,
                    CreatedBy = _user.UserCode,
                    CreatedOn = now,
                });
            }

            var groupPoIds = billable.Select(x => x.Line.PoId).Distinct().ToList();
            var threeWay = billable.All(x => grnPoLineIds.Contains(x.Line.PoLineId));

            var invoice = new Invoice
            {
                Id = invoiceId,
                // Placeholder — the supplier sets the REAL number in Draft; submit rejects the DRAFT- prefix.
                InvoiceNumber = $"DRAFT-{asn.AsnNumber}-{groupSeq}",
                PurchaseOrderId = groupPoIds.Count == 1 ? groupPoIds[0] : null,
                AsnId = asn.Id,
                SupplierId = asn.SupplierId,
                InvoiceDate = now.Date,
                InvoiceAmount = invoiceAmount,
                TaxAmount = taxAmount,
                NetAmount = invoiceAmount + taxAmount,
                CurrencyCode = NormalizeCurrency(group.Key.Code),
                MatchingType = threeWay ? MatchingType.ThreeWay : MatchingType.TwoWay,
                InvoiceStatus = InvoiceStatus.Draft,
                InvoiceOrigin = InvoiceOrigin.AsnGenerated,
                SeccodeId = asn.SeccodeId,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            };
            foreach (var l in invoiceLines) invoice.Lines.Add(l);

            _db.Invoices.Add(invoice);
            invoices.Add(invoice);
        }

        if (invoices.Count == 0)
        {
            // Every group's remaining balance is 0 — nothing to invoice. Quiet no-op; status untouched.
            return new Outcome(Array.Empty<Invoice>(), Created: false, Blocked: false, BlockNote: null);
        }

        asn.InvoiceGenerationStatus = StatusGenerated;
        asn.InvoiceGenerationNote = null;
        return new Outcome(invoices, Created: true, Blocked: false, BlockNote: null);
    }

    private static string NormalizeCurrency(string? code)
        => string.IsNullOrWhiteSpace(code) ? "INR" : code.Trim().ToUpperInvariant();

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
