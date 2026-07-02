using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Documents;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Application.SystemSettings.Invoicing;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

/// <summary>
/// R6 (2026-07-02) — submits a <b>Draft</b> invoice. Guard order (plan §2.4):
/// <list type="number">
///   <item>Draft-only (409) → real-number (rejects the <c>DRAFT-</c> and legacy <c>INV-DRAFT-</c> placeholder
///         prefixes) → invoiceDate → IRN/e-way-bill per the <c>Invoicing</c> settings category (plan D11) →
///         (supplier, invoiceNumber) uniqueness re-check;</item>
///   <item>attachment governance (mandatory-block 400 / warning-confirm — BEFORE any mutation);</item>
///   <item>then, inside ONE explicit transaction: tax-rate re-resolve + freeze (drift ⇒ advisory notice, last
///         write wins — lines are immutable after Submit since edits are Draft-only), the ATOMIC per-PO-line
///         over-invoice reservation (conditional <c>ExecuteUpdateAsync</c>
///         <c>WHERE invoicedQtyToDate + billed ≤ shippedQtyToDate</c>; 0 rows ⇒ 409 and the rollback reverts the
///         sibling reservations), and local matching — TwoWay: reservation success = matched; ThreeWay:
///         additionally billed ≤ Σ ReceivedQty of the covering GRNs. Header lands
///         <see cref="InvoiceStatus.Matched"/> (all pass) or <see cref="InvoiceStatus.MatchExceptions"/>
///         (plan D7/D9); SubmittedBy/At stamped regardless.</item>
/// </list>
/// NOT posted to ERP here — posting stays GRN-gated (the auto-post claim now accepts Submitted or Matched).
/// Advisory rate-drift notes ride the response envelope (<c>Result.notices</c>).
/// </summary>
public record SubmitInvoiceCommand(Guid Id, SubmitInvoiceRequest Body) : IRequest<SubmitOutcome<InvoiceDetailDto>>;

public class SubmitInvoiceCommandValidator : AbstractValidator<SubmitInvoiceCommand>
{
    public SubmitInvoiceCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class SubmitInvoiceCommandHandler : IRequestHandler<SubmitInvoiceCommand, SubmitOutcome<InvoiceDetailDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMediator _mediator;
    private readonly AttachmentSubmitGuard _attachmentGuard;
    private readonly TaxRateResolver _taxResolver;
    private readonly IInvoicingSettings _invoicing;

    public SubmitInvoiceCommandHandler(
        IAppDbContext db, ICurrentUser user, IMediator mediator, AttachmentSubmitGuard attachmentGuard,
        TaxRateResolver taxResolver, IInvoicingSettings invoicing)
    {
        _db = db; _user = user; _mediator = mediator; _attachmentGuard = attachmentGuard;
        _taxResolver = taxResolver; _invoicing = invoicing;
    }

    public async Task<SubmitOutcome<InvoiceDetailDto>> Handle(SubmitInvoiceCommand request, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.Id, ct)
                      ?? throw new NotFoundException("Invoice", request.Id);

        if (invoice.InvoiceStatus != InvoiceStatus.Draft)
            throw new ConflictException($"Invoice is '{invoice.InvoiceStatus}'; only a Draft invoice can be submitted.");

        // ---- Mandatory fields + e-invoice compliance gates (plan D11) -----------------------------------
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            || invoice.InvoiceNumber.StartsWith("DRAFT-", StringComparison.OrdinalIgnoreCase)
            || invoice.InvoiceNumber.StartsWith("INV-DRAFT-", StringComparison.OrdinalIgnoreCase))
            errors["invoiceNumber"] = new[] { "A real invoice number is required before submit (replace the draft placeholder)." };
        if (invoice.InvoiceDate == default)
            errors["invoiceDate"] = new[] { "Invoice date is required." };
        if (_invoicing.RequireIrn && string.IsNullOrWhiteSpace(invoice.EInvoiceIrn))
            errors["eInvoiceIrn"] = new[] { "An e-invoice IRN is required before submit (Invoicing.RequireIrn is ON)." };
        if (_invoicing.RequireEWayBill && string.IsNullOrWhiteSpace(invoice.EWayBillNumber))
            errors["eWayBillNumber"] = new[] { "An e-way bill number is required before submit (Invoicing.RequireEWayBill is ON)." };
        if (errors.Count > 0)
            throw new ValidationException(errors);

        // ---- (supplier, invoiceNumber) uniqueness re-check ----------------------------------------------
        var dup = await _db.Invoices.AnyAsync(
            i => i.SupplierId == invoice.SupplierId && i.InvoiceNumber == invoice.InvoiceNumber && i.Id != invoice.Id, ct);
        if (dup)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["invoiceNumber"] = new[] { $"Invoice number '{invoice.InvoiceNumber}' already exists for this supplier." }
            });

        var now = DateTime.UtcNow;

        // ---- Attachment Requirement Governance (§8.3) — BEFORE any mutation -----------------------------
        var attachmentDecision = await _attachmentGuard.EvaluateAsync(
            _db, DocumentOwnerTypes.Invoice, invoice.Id, invoice.InvoiceNumber, invoice.SupplierId,
            request.Body.AcknowledgeMissingAttachments, invoice.TenantId, now, ct);
        if (attachmentDecision.RequiresConfirmation)
            return SubmitOutcome<InvoiceDetailDto>.Confirm(attachmentDecision.MissingWarning);

        // ==================================================================================================
        // Mutation phase — ONE explicit transaction: rate freeze + reservation + matching + status flip.
        // ==================================================================================================
        var notices = new List<string>();
        await using (var tx = await _db.BeginTransactionAsync(ct))
        {
            var lines = await _db.InvoiceLines.Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct);

            // ---- Re-resolve + FREEZE the current tax rate per line (drift ⇒ advisory, last write wins) ---
            var resolved = await _taxResolver.ResolveAsync(
                lines.Where(l => l.TaxId.HasValue).Select(l => l.TaxId!.Value), invoice.TenantId, ct);

            foreach (var line in lines.Where(l => l.TaxId.HasValue))
            {
                if (!resolved.TryGetValue(line.TaxId!.Value, out var tax))
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["lines"] = new[]
                        {
                            $"The tax on item '{line.ItemCode}' ('{line.TaxCode}') no longer resolves; reselect a valid tax code."
                        }
                    });
                if (tax.Rate is null)
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["lines"] = new[]
                        {
                            $"Tax '{tax.Code}' on item '{line.ItemCode}' has no rate; the invoice cannot be submitted until the rate is configured."
                        }
                    });

                if (line.TaxRatePct != tax.Rate)
                {
                    notices.Add($"Tax {tax.Code}: rate changed {FormatRate(line.TaxRatePct)}% → {FormatRate(tax.Rate)}%");
                    line.TaxRatePct = tax.Rate;
                    line.TaxCode = tax.Code;
                    line.TaxDescription = tax.Description;
                    line.TaxAmount = decimal.Round(line.LineAmount * tax.Rate.Value / 100m, 2);
                    line.UpdatedBy = _user.UserCode;
                    line.UpdatedOn = now;
                }
            }

            if (notices.Count > 0)
            {
                invoice.InvoiceAmount = lines.Sum(l => l.LineAmount);
                invoice.TaxAmount = lines.Sum(l => l.TaxAmount);
                invoice.NetAmount = invoice.InvoiceAmount + invoice.TaxAmount;
            }

            // ---- ATOMIC per-PO-line over-invoice reservation (mirrors the ASN over-ship guard) -----------
            // Aggregate billed per PO line FIRST (an invoice normally holds one line per PO line — be safe),
            // so the conditional ceiling is evaluated once per line against the FULL requested quantity.
            var perPoLine = lines
                .GroupBy(l => l.PurchaseOrderLineId)
                .Select(g => new { PoLineId = g.Key, Qty = g.Sum(x => x.BilledQty), ItemCode = g.First().ItemCode })
                .ToList();

            // DI-02 — lock ordering: X-lock the PO lines in ascending PurchaseOrderLineId so concurrent
            // multi-line submitters never acquire them in opposite orders (deadlock).
            foreach (var x in perPoLine.Where(x => x.Qty > 0).OrderBy(x => x.PoLineId))
            {
                var qty = x.Qty;
                var affected = await _db.PurchaseOrderLines
                    .Where(l => l.Id == x.PoLineId && l.InvoicedQtyToDate + qty <= l.ShippedQtyToDate)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        l => l.InvoicedQtyToDate, l => l.InvoicedQtyToDate + qty), ct);

                // 0 rows = the conditional ceiling failed — a concurrent submit already reserved the balance.
                // The throw disposes the transaction, rolling back every sibling reservation taken above.
                if (affected == 0)
                    throw new ConflictException(
                        $"Billed qty {qty} for item '{x.ItemCode}' exceeds the remaining invoiceable balance " +
                        "(shipped − already invoiced). Another invoice may have reserved it first — reload and retry.");
            }

            // ---- Local matching (plan D7/D9) -------------------------------------------------------------
            // TwoWay: the reservation passing IS the match. ThreeWay: additionally, per PO line,
            // billed ≤ Σ ReceivedQty of the covering GRNs (ASN-scoped when the invoice is ASN-linked).
            var allMatched = true;
            if (invoice.MatchingType == MatchingType.ThreeWay)
            {
                var poLineIds = perPoLine.Select(x => x.PoLineId).ToList();
                var grnQuery = _db.GoodsReceipts.Where(g => poLineIds.Contains(g.PurchaseOrderLineId));
                if (invoice.AsnId.HasValue)
                {
                    var asnId = invoice.AsnId.Value;
                    grnQuery = grnQuery.Where(g => g.AsnId == asnId);
                }
                var grnSums = await grnQuery
                    .GroupBy(g => g.PurchaseOrderLineId)
                    .Select(g => new { PoLineId = g.Key, Received = g.Sum(x => x.ReceivedQty) })
                    .ToDictionaryAsync(g => g.PoLineId, g => g.Received, ct);

                allMatched = perPoLine.All(x =>
                    x.Qty <= (grnSums.TryGetValue(x.PoLineId, out var received) ? received : 0m));
            }

            invoice.InvoiceStatus = allMatched ? InvoiceStatus.Matched : InvoiceStatus.MatchExceptions;
            invoice.SubmittedBy = _user.UserCode;   // stamped regardless of the matching outcome.
            invoice.SubmittedAt = now;
            invoice.UpdatedBy = _user.UserCode;
            invoice.UpdatedOn = now;

            await _db.SaveChangesAsync(ct);   // NO ERP post — posting is GRN-gated (Module 5).
            await tx.CommitAsync(ct);
        }

        var dto = await _mediator.Send(new GetInvoiceByIdQuery(invoice.Id), ct);
        return SubmitOutcome<InvoiceDetailDto>.Completed(dto, notices);
    }

    private static string FormatRate(decimal? rate)
        => rate is { } r ? r.ToString("0.####") : "—";
}
