using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 4. Manually create the ONE draft invoice spanning an ASN's POs (Q1b). The same
/// creation also runs automatically inside <c>SubmitAsnCommand</c>; both go through
/// <see cref="DraftInvoiceFromAsnFactory"/> so the UQ_Invoice_asnId upsert-or-skip and the mixed-currency guard
/// (R16) are enforced in one place. If a draft already exists for the ASN it is returned (idempotent), never a
/// second insert. NO ERP post on create (posting is GRN-gated in Module 5).
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

        // An invoice is only meaningful for a submitted ASN (the auto path fires on submit). Allow manual create
        // for Submitted ASNs that somehow have no invoice yet; reject Draft/Cancelled.
        if (asn.AsnStatus != AsnStatus.Submitted)
            throw new ConflictException($"ASN is '{asn.AsnStatus}'; an invoice can only be created from a Submitted ASN.");

        var now = DateTime.UtcNow;
        var result = await _factory.EnsureDraftAsync(asn, now, ct);
        if (result.Created)
            await _db.SaveChangesAsync(ct);

        return await _mediator.Send(new GetInvoiceByIdQuery(result.Invoice.Id), ct);
    }
}
