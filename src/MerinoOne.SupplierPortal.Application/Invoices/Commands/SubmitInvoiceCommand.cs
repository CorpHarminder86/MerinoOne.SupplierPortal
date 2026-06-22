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
/// R4 (2026-06-22) — Module 4. Submits a <b>Draft</b> invoice (Draft -> Submitted). Validates the Q8 mandatory
/// fields (invoiceNumber + invoiceDate; the placeholder "INV-DRAFT-…" number from the ASN auto-create must be
/// replaced). e-Invoice fields (IRN / Ack / eWayBill) are conditionally required per the GST regime — left as a
/// soft, configurable check here. On submit the invoice becomes ELIGIBLE for LN posting but is <b>NOT posted</b>
/// (posting is GRN-gated, Module 5). No outbox enqueue here.
/// </summary>
public record SubmitInvoiceCommand(Guid Id, SubmitInvoiceRequest Body) : IRequest<InvoiceDetailDto>;

public class SubmitInvoiceCommandValidator : AbstractValidator<SubmitInvoiceCommand>
{
    public SubmitInvoiceCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class SubmitInvoiceCommandHandler : IRequestHandler<SubmitInvoiceCommand, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMediator _mediator;

    public SubmitInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, IMediator mediator)
    {
        _db = db; _user = user; _mediator = mediator;
    }

    public async Task<InvoiceDetailDto> Handle(SubmitInvoiceCommand request, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.Id, ct)
                      ?? throw new NotFoundException("Invoice", request.Id);

        if (invoice.InvoiceStatus != InvoiceStatus.Draft)
            throw new ConflictException($"Invoice is '{invoice.InvoiceStatus}'; only a Draft invoice can be submitted.");

        // Q8 mandatory fields.
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            || invoice.InvoiceNumber.StartsWith("INV-DRAFT-", StringComparison.OrdinalIgnoreCase))
            errors["invoiceNumber"] = new[] { "A real invoice number is required before submit (replace the draft placeholder)." };
        if (invoice.InvoiceDate == default)
            errors["invoiceDate"] = new[] { "Invoice date is required." };
        if (errors.Count > 0)
            throw new ValidationException(errors);

        var now = DateTime.UtcNow;
        invoice.InvoiceStatus = InvoiceStatus.Submitted;   // eligible for the GRN-gated posting set (Module 5).
        invoice.SubmittedBy = _user.UserCode;
        invoice.SubmittedAt = now;
        invoice.UpdatedBy = _user.UserCode;
        invoice.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);   // NO ERP post — posting is GRN-gated (Module 5).

        return await _mediator.Send(new GetInvoiceByIdQuery(invoice.Id), ct);
    }
}
