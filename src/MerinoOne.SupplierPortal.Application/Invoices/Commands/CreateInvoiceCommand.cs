using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 4. REFACTORED off auto-post-on-create: a manually-created invoice is now a <b>Draft</b>
/// (was Submitted) and there is NO create-time outbox/ERP post (the Increment-0 enqueue is removed). Posting is a
/// separate, GRN-gated step (Module 5); the supplier submits via <c>SubmitInvoiceCommand</c> first. The ASN-driven
/// path uses <c>CreateInvoiceFromAsnCommand</c> instead.
/// </summary>
public record CreateInvoiceCommand(CreateInvoiceRequest Body) : IRequest<InvoiceDetailDto>;

public class CreateInvoiceCommandValidator : AbstractValidator<CreateInvoiceCommand>
{
    public CreateInvoiceCommandValidator()
    {
        RuleFor(x => x.Body.PurchaseOrderId).NotEmpty();
        RuleFor(x => x.Body.InvoiceNumber).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.InvoiceDate).NotEmpty();
        RuleFor(x => x.Body.CurrencyCode).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Body.MatchingType).NotEmpty()
            .Must(v => v == nameof(MatchingType.TwoWay) || v == nameof(MatchingType.ThreeWay))
            .WithMessage("MatchingType must be 'TwoWay' or 'ThreeWay'.");
        RuleFor(x => x.Body.InvoiceAmount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Body.TaxAmount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Body.NetAmount).GreaterThan(0);
        RuleFor(x => x.Body.Lines).NotNull().NotEmpty()
            .WithMessage("At least one invoice line is required.");
        RuleForEach(x => x.Body.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PurchaseOrderLineId).NotEmpty();
            line.RuleFor(l => l.ItemCode).NotEmpty().MaximumLength(50);
            line.RuleFor(l => l.BilledQty).GreaterThan(0);
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.LineAmount).GreaterThanOrEqualTo(0);
        });
    }
}

internal sealed record ResolvedTaxSnapshot(Guid TaxId, string Code, string Description, decimal Rate);

public class CreateInvoiceCommandHandler : IRequestHandler<CreateInvoiceCommand, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMediator _mediator;
    private readonly TaxRateResolver _taxResolver;

    public CreateInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, IMediator mediator, TaxRateResolver taxResolver)
    {
        _db = db; _user = user; _mediator = mediator; _taxResolver = taxResolver;
    }

    public async Task<InvoiceDetailDto> Handle(CreateInvoiceCommand request, CancellationToken ct)
    {
        var body = request.Body;

        var po = await _db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == body.PurchaseOrderId, ct)
                 ?? throw new NotFoundException("PurchaseOrder", body.PurchaseOrderId);

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == po.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", po.SupplierId);

        // Validate all PO lines belong to this PO
        var requestedLineIds = body.Lines.Select(l => l.PurchaseOrderLineId).ToList();
        var poLines = await _db.PurchaseOrderLines
            .Where(l => l.PurchaseOrderId == po.Id && requestedLineIds.Contains(l.Id))
            .Select(l => new { l.Id, l.DiscountPct, l.DiscountAmount, l.TaxId })
            .ToListAsync(ct);
        var poLineById = poLines.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        var validPoLineIds = poLineById.Keys.ToList();

        var invalid = requestedLineIds.Except(validPoLineIds).ToList();
        if (invalid.Count > 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { $"PurchaseOrderLineId(s) not on PO: {string.Join(", ", invalid)}" }
            });
        }

        // Validate ASN, if supplied, covers the supplied PO (header scalar OR junction — multi-PO aware).
        if (body.AsnId.HasValue)
        {
            var asnExists = await _db.Asns.AnyAsync(a => a.Id == body.AsnId.Value, ct);
            if (!asnExists)
                throw new NotFoundException("Asn", body.AsnId.Value);

            var coversPo = await _db.Asns.AnyAsync(a => a.Id == body.AsnId.Value && a.PurchaseOrderId == po.Id, ct)
                || await _db.AsnPurchaseOrders.AnyAsync(j => j.AsnId == body.AsnId.Value && j.PurchaseOrderId == po.Id && !j.IsDeleted, ct);
            if (!coversPo)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["asnId"] = new[] { "ASN does not cover the supplied PurchaseOrderId." }
                });
        }

        // Sum(line amounts) ~= net amount
        var lineSum = body.Lines.Sum(l => l.LineAmount);
        if (Math.Abs(lineSum - body.NetAmount) > 0.01m)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["netAmount"] = new[] { $"Sum of line amounts ({lineSum}) must equal NetAmount ({body.NetAmount})." }
            });
        }

        // Uniqueness check on (supplierId, invoiceNumber) is enforced at DB level too
        var dup = await _db.Invoices.AnyAsync(i => i.SupplierId == po.SupplierId && i.InvoiceNumber == body.InvoiceNumber, ct);
        if (dup)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["invoiceNumber"] = new[] { $"Invoice number '{body.InvoiceNumber}' already exists for this supplier." }
            });
        }

        var matchingType = body.MatchingType == nameof(MatchingType.TwoWay)
            ? MatchingType.TwoWay
            : MatchingType.ThreeWay;

        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceNumber = body.InvoiceNumber,
            PurchaseOrderId = po.Id,
            AsnId = body.AsnId,
            SupplierId = po.SupplierId,
            InvoiceDate = body.InvoiceDate,
            // Header totals are set below from the server-recomputed lines (R19) — never the client's figures.
            InvoiceAmount = 0m,
            TaxAmount = 0m,
            NetAmount = 0m,
            CurrencyCode = body.CurrencyCode,
            MatchingType = matchingType,
            // R4: REFACTORED — created as Draft (was Submitted). The supplier submits via SubmitInvoiceCommand;
            // posting is GRN-gated (Module 5). No create-time ERP post.
            InvoiceStatus = InvoiceStatus.Draft,
            EInvoiceIrn = body.EInvoiceIrn,
            EInvoiceAckNo = body.EInvoiceAckNo,
            EWayBillNumber = body.EWayBillNumber,
            // SubmittedBy/SubmittedAt are stamped by SubmitInvoiceCommand — a freshly-created invoice is a Draft.
            Notes = body.Notes,
            SeccodeId = supplier.SeccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };

        // R19 — the server snapshots the PO line's discount and recomputes the taxable LineAmount (gross − discount);
        // the client's LineAmount is NOT trusted. The PO line's DiscountAmount is PER UNIT, so the stored line discount
        // is perUnit × billedQty. Tax: the request carries only a tax CODE + amount (no rate), so the client TaxAmount
        // is kept as-is (the code-only behaviour); Submit re-resolves the governed rate and recomputes it.
        // Tax: the governed rate is resolved server-side from the PO line's TaxId (same source as the ASN generator) and
        // levied on the NET (discounted) LineAmount, snapshotting TaxId/Code/Description/Rate on the line. When the
        // client typed a DIFFERENT tax code than the PO line's governed one, or no rate resolves, it is a code-only tax:
        // the client TaxCode/TaxAmount are kept as-is (existing behaviour — no rate to recompute with).
        var resolvedTaxes = await _taxResolver.ResolveAsync(
            poLineById.Values.Where(p => p.TaxId.HasValue).Select(p => p.TaxId!.Value), po.TenantId, ct);

        decimal invoiceAmount = 0m, taxAmount = 0m;
        foreach (var line in body.Lines)
        {
            var pol = poLineById[line.PurchaseOrderLineId];
            var lineDiscount = InvoiceLineMath.LineDiscount(
                InvoiceLineMath.PerUnitDiscount(pol.DiscountAmount, pol.DiscountPct, line.UnitPrice), line.BilledQty);
            var lineAmount = InvoiceLineMath.Taxable(line.BilledQty, line.UnitPrice, lineDiscount);

            ResolvedTaxSnapshot? snap = null;
            if (pol.TaxId.HasValue
                && resolvedTaxes.TryGetValue(pol.TaxId.Value, out var rt)
                && rt.Rate is not null
                && (string.IsNullOrWhiteSpace(line.TaxCode)
                    || string.Equals(line.TaxCode.Trim(), rt.Code, StringComparison.OrdinalIgnoreCase)))
                snap = new ResolvedTaxSnapshot(pol.TaxId.Value, rt.Code, rt.Description, rt.Rate.Value);

            var lineTax = snap is null ? line.TaxAmount : InvoiceLineMath.Tax(lineAmount, snap.Rate);
            invoiceAmount += lineAmount;
            taxAmount += lineTax;
            invoice.Lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoiceId,
                PurchaseOrderLineId = line.PurchaseOrderLineId,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                BilledQty = line.BilledQty,
                UnitPrice = line.UnitPrice,
                LineAmount = lineAmount,
                DiscountPct = pol.DiscountPct,
                DiscountAmount = lineDiscount,
                TaxCode = snap?.Code ?? line.TaxCode,
                TaxDescription = snap?.Description,
                TaxId = snap?.TaxId,
                TaxRatePct = snap?.Rate,
                TaxAmount = lineTax,
                CreatedBy = _user.UserCode,
                CreatedOn = DateTime.UtcNow,
            });
        }

        invoice.InvoiceAmount = invoiceAmount;
        invoice.TaxAmount = taxAmount;
        invoice.NetAmount = invoiceAmount + taxAmount;

        _db.Invoices.Add(invoice);

        // R4: NO create-time ERP post. The invoice is a Draft; posting is a later GRN-gated step (Module 5).
        await _db.SaveChangesAsync(ct);

        // Single source of truth for the reshaped (nullable-PO, multi-PO, lifecycle, rowVersion) detail shape.
        return await _mediator.Send(new Queries.GetInvoiceByIdQuery(invoice.Id), ct);
    }
}
