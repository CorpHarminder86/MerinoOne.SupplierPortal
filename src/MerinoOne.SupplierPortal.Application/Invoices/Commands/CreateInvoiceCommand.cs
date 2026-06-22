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

public class CreateInvoiceCommandHandler : IRequestHandler<CreateInvoiceCommand, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMediator _mediator;

    public CreateInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, IMediator mediator)
    {
        _db = db; _user = user; _mediator = mediator;
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
        var validPoLineIds = await _db.PurchaseOrderLines
            .Where(l => l.PurchaseOrderId == po.Id && requestedLineIds.Contains(l.Id))
            .Select(l => l.Id)
            .ToListAsync(ct);

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
            InvoiceAmount = body.InvoiceAmount,
            TaxAmount = body.TaxAmount,
            NetAmount = body.NetAmount,
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

        foreach (var line in body.Lines)
        {
            invoice.Lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoiceId,
                PurchaseOrderLineId = line.PurchaseOrderLineId,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                BilledQty = line.BilledQty,
                UnitPrice = line.UnitPrice,
                LineAmount = line.LineAmount,
                TaxCode = line.TaxCode,
                TaxAmount = line.TaxAmount,
                CreatedBy = _user.UserCode,
                CreatedOn = DateTime.UtcNow,
            });
        }

        _db.Invoices.Add(invoice);

        // R4: NO create-time ERP post. The invoice is a Draft; posting is a later GRN-gated step (Module 5).
        await _db.SaveChangesAsync(ct);

        // Single source of truth for the reshaped (nullable-PO, multi-PO, lifecycle, rowVersion) detail shape.
        return await _mediator.Send(new Queries.GetInvoiceByIdQuery(invoice.Id), ct);
    }
}
