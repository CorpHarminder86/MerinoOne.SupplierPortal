using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

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
    private readonly IInforIntegrationService _infor;

    public CreateInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, IInforIntegrationService infor)
    {
        _db = db; _user = user; _infor = infor;
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

        // Validate ASN, if supplied, points to same PO
        if (body.AsnId.HasValue)
        {
            var asnPoId = await _db.Asns
                .Where(a => a.Id == body.AsnId.Value)
                .Select(a => (Guid?)a.PurchaseOrderId)
                .FirstOrDefaultAsync(ct);
            if (asnPoId == null)
                throw new NotFoundException("Asn", body.AsnId.Value);
            if (asnPoId != po.Id)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["asnId"] = new[] { "ASN does not belong to the supplied PurchaseOrderId." }
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
            InvoiceStatus = InvoiceStatus.Submitted,
            EInvoiceIrn = body.EInvoiceIrn,
            EInvoiceAckNo = body.EInvoiceAckNo,
            EWayBillNumber = body.EWayBillNumber,
            SubmittedBy = _user.UserCode,
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

        var sync = await _infor.SubmitInvoiceAsync(invoiceId, ct);
        _db.InforSyncLogs.Add(new InforSyncLog
        {
            Id = Guid.NewGuid(),
            EntityName = "Invoice",
            Direction = SyncDirection.Outbound,
            Status = sync.Success ? SyncStatus.Success : SyncStatus.Failed,
            IdempotencyKey = sync.IdempotencyKey,
            SyncedAt = DateTime.UtcNow,
            ErrorMessage = sync.Success ? null : sync.Message,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);

        var lineDtos = invoice.Lines
            .Select(l => new InvoiceLineDto(
                l.Id,
                l.PurchaseOrderLineId,
                l.ItemCode,
                l.ItemDescription,
                l.BilledQty,
                l.UnitPrice,
                l.LineAmount,
                l.TaxCode,
                l.TaxAmount))
            .ToList();

        string? asnNumber = null;
        if (invoice.AsnId.HasValue)
        {
            asnNumber = await _db.Asns
                .Where(a => a.Id == invoice.AsnId.Value)
                .Select(a => a.AsnNumber)
                .FirstOrDefaultAsync(ct);
        }

        return new InvoiceDetailDto(
            invoice.Id,
            invoice.Seq,
            invoice.InvoiceNumber,
            invoice.PurchaseOrderId,
            po.PoNumber,
            invoice.AsnId,
            asnNumber,
            invoice.SupplierId,
            supplier.LegalName,
            supplier.SupplierCode,
            invoice.InvoiceDate,
            invoice.InvoiceAmount,
            invoice.TaxAmount,
            invoice.NetAmount,
            invoice.CurrencyCode,
            invoice.MatchingType.ToString(),
            invoice.GrnReference,
            invoice.InvoiceStatus.ToString(),
            invoice.RejectionReason,
            invoice.EInvoiceIrn,
            invoice.EInvoiceAckNo,
            invoice.EWayBillNumber,
            invoice.SubmittedBy,
            invoice.ApprovedBy,
            invoice.ApprovedAt,
            invoice.Notes,
            invoice.CreatedOn,
            lineDtos);
    }
}
