using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Documents;
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
// R4 (2026-06-26) — Phase 4 / §8.3 / UC-ATT-03: returns SubmitOutcome<InvoiceDetailDto> so the Warning "confirm
// to proceed" attachment path can be modelled WITHOUT throwing (Mandatory-missing still throws → 400).
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

    public SubmitInvoiceCommandHandler(
        IAppDbContext db, ICurrentUser user, IMediator mediator, AttachmentSubmitGuard attachmentGuard)
    {
        _db = db; _user = user; _mediator = mediator; _attachmentGuard = attachmentGuard;
    }

    public async Task<SubmitOutcome<InvoiceDetailDto>> Handle(SubmitInvoiceCommand request, CancellationToken ct)
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

        // ---- Attachment Requirement Governance (Phase 4 / §8.3, UC-ATT-01..05) --------------------------
        // Entity "Invoice", supplier = this invoice's supplier. Mandatory-missing throws (400); Warning-missing +
        // not-acknowledged returns ConfirmationRequired WITHOUT mutating the invoice; acknowledged-skip stages the
        // skip AuditEntry that commits with the status flip below (same transaction).
        var attachmentDecision = await _attachmentGuard.EvaluateAsync(
            _db, DocumentOwnerTypes.Invoice, invoice.Id, invoice.InvoiceNumber, invoice.SupplierId,
            request.Body.AcknowledgeMissingAttachments, invoice.TenantId, now, ct);
        if (attachmentDecision.RequiresConfirmation)
            return SubmitOutcome<InvoiceDetailDto>.Confirm(attachmentDecision.MissingWarning);

        invoice.InvoiceStatus = InvoiceStatus.Submitted;   // eligible for the GRN-gated posting set (Module 5).
        invoice.SubmittedBy = _user.UserCode;
        invoice.SubmittedAt = now;
        invoice.UpdatedBy = _user.UserCode;
        invoice.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);   // NO ERP post — posting is GRN-gated (Module 5).

        var dto = await _mediator.Send(new GetInvoiceByIdQuery(invoice.Id), ct);
        return SubmitOutcome<InvoiceDetailDto>.Completed(dto);
    }
}
