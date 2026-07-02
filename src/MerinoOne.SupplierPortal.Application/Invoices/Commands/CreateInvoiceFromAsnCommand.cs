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
/// R6 (2026-07-02) — manual/Retry trigger for the grouped draft-invoice generation (the same generator runs
/// automatically inside the ASN approve transaction). Idempotent: existing (non-deleted) invoices for the ASN are
/// returned, never a second insert. Doubles as the <b>Retry generation</b> endpoint for a Blocked ASN (plan D10):
/// a successful run clears the Blocked flag; a still-blocked run persists the refreshed note and returns 400 with
/// the reason. NO ERP post on create (posting is GRN-gated, Module 5).
/// </summary>
public record CreateInvoiceFromAsnCommand(CreateInvoiceFromAsnRequest Body) : IRequest<InvoiceDetailDto>;

public class CreateInvoiceFromAsnCommandValidator : AbstractValidator<CreateInvoiceFromAsnCommand>
{
    public CreateInvoiceFromAsnCommandValidator() => RuleFor(x => x.Body.AsnId).NotEmpty();
}

public class CreateInvoiceFromAsnCommandHandler : IRequestHandler<CreateInvoiceFromAsnCommand, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly DraftInvoiceFromAsnFactory _factory;
    private readonly IMediator _mediator;

    public CreateInvoiceFromAsnCommandHandler(IAppDbContext db, DraftInvoiceFromAsnFactory factory, IMediator mediator)
    {
        _db = db; _factory = factory; _mediator = mediator;
    }

    public async Task<InvoiceDetailDto> Handle(CreateInvoiceFromAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Body.AsnId, ct)
                  ?? throw new NotFoundException("Asn", request.Body.AsnId);

        // Generation is only meaningful for a submitted ASN (the auto path fires at approve→submit).
        if (asn.AsnStatus != AsnStatus.Submitted)
            throw new ConflictException($"ASN is '{asn.AsnStatus}'; an invoice can only be created from a Submitted ASN.");

        var now = DateTime.UtcNow;
        var outcome = await _factory.EnsureDraftAsync(asn, now, ct);

        // Persist whatever the generator staged: new drafts + Generated flag, OR the (re-)Blocked flag/note +
        // buyer notification rows. Saving BEFORE the Blocked throw keeps the ASN flag authoritative for the UI.
        await _db.SaveChangesAsync(ct);

        if (outcome.Blocked)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["invoiceGeneration"] = new[] { outcome.BlockNote ?? "Invoice generation is blocked for this ASN." }
            });

        if (outcome.Invoices.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["invoiceGeneration"] = new[]
                {
                    "Nothing to invoice: every shipped quantity on this ASN's PO lines is already invoiced."
                }
            });

        return await _mediator.Send(new GetInvoiceByIdQuery(outcome.Invoices[0].Id), ct);
    }
}
