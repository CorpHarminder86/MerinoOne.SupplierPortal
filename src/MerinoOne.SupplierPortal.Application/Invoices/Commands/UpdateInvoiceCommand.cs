using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 4. Edits a <b>Draft</b> invoice only (409 once Submitted). Editable set:
/// invoiceNumber, invoiceDate, eInvoiceIrn, eInvoiceAckNo, eWayBillNumber, notes. Amounts/lines are inherited
/// from the ASN at create and are NOT edited here. Enforces the (supplier, invoiceNumber) uniqueness rule.
/// </summary>
public record UpdateInvoiceCommand(Guid Id, UpdateInvoiceRequest Body) : IRequest<InvoiceDetailDto>;

public class UpdateInvoiceCommandValidator : AbstractValidator<UpdateInvoiceCommand>
{
    public UpdateInvoiceCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.InvoiceNumber).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.InvoiceDate).NotEmpty();
        RuleFor(x => x.Body.EInvoiceIrn).MaximumLength(100);
        RuleFor(x => x.Body.EInvoiceAckNo).MaximumLength(100);
        RuleFor(x => x.Body.EWayBillNumber).MaximumLength(100);
    }
}

public class UpdateInvoiceCommandHandler : IRequestHandler<UpdateInvoiceCommand, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMediator _mediator;

    public UpdateInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, IMediator mediator)
    {
        _db = db; _user = user; _mediator = mediator;
    }

    public async Task<InvoiceDetailDto> Handle(UpdateInvoiceCommand request, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.Id, ct)
                      ?? throw new NotFoundException("Invoice", request.Id);

        if (invoice.InvoiceStatus != InvoiceStatus.Draft)
            throw new ConflictException($"Invoice is '{invoice.InvoiceStatus}'; only a Draft invoice can be edited.");

        var body = request.Body;
        var trimmedNumber = body.InvoiceNumber.Trim();

        // (supplier, invoiceNumber) uniqueness — excluding this invoice.
        if (!string.Equals(trimmedNumber, invoice.InvoiceNumber, StringComparison.Ordinal))
        {
            var dup = await _db.Invoices.AnyAsync(
                i => i.SupplierId == invoice.SupplierId && i.InvoiceNumber == trimmedNumber && i.Id != invoice.Id, ct);
            if (dup)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["invoiceNumber"] = new[] { $"Invoice number '{trimmedNumber}' already exists for this supplier." }
                });
        }

        invoice.InvoiceNumber = trimmedNumber;
        invoice.InvoiceDate = body.InvoiceDate;
        invoice.EInvoiceIrn = string.IsNullOrWhiteSpace(body.EInvoiceIrn) ? null : body.EInvoiceIrn.Trim();
        invoice.EInvoiceAckNo = string.IsNullOrWhiteSpace(body.EInvoiceAckNo) ? null : body.EInvoiceAckNo.Trim();
        invoice.EWayBillNumber = string.IsNullOrWhiteSpace(body.EWayBillNumber) ? null : body.EWayBillNumber.Trim();
        invoice.Notes = body.Notes;
        invoice.UpdatedBy = _user.UserCode;
        invoice.UpdatedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return await _mediator.Send(new GetInvoiceByIdQuery(invoice.Id), ct);
    }
}
