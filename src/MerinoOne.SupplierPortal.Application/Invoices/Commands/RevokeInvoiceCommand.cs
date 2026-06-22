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
/// R4 (2026-06-22) — Module 4 (Q9). Admin <b>pre-post</b> revoke of an invoice: <c>Submitted -> Draft</c>. This is
/// a pure status flip — NO LN reversal call (auto-post only picks Submitted, so reverting to Draft removes it from
/// the posting set; nothing was posted yet). Because a concurrent GRN-gated auto-post could be racing this revoke,
/// the operation is guarded:
/// <list type="bullet">
///   <item>policy <c>Invoice.Revoke</c> (admin/Finance — already seeded) on the controller;</item>
///   <item>state guard: only <c>Submitted</c> AND <c>erpPostedAt IS NULL</c> (pre-post) — else 409;</item>
///   <item>optimistic concurrency: the client's RowVersion is applied as the EF concurrency token; a stale token
///         (someone else changed the row) yields 409, never a silent overwrite.</item>
/// </list>
/// </summary>
public record RevokeInvoiceCommand(Guid Id, RevokeInvoiceRequest Body) : IRequest<InvoiceDetailDto>;

public class RevokeInvoiceCommandValidator : AbstractValidator<RevokeInvoiceCommand>
{
    public RevokeInvoiceCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Reason).MaximumLength(1000);
    }
}

public class RevokeInvoiceCommandHandler : IRequestHandler<RevokeInvoiceCommand, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMediator _mediator;

    public RevokeInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, IMediator mediator)
    {
        _db = db; _user = user; _mediator = mediator;
    }

    public async Task<InvoiceDetailDto> Handle(RevokeInvoiceCommand request, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.Id, ct)
                      ?? throw new NotFoundException("Invoice", request.Id);

        // Pre-post only (Q9): must be Submitted and not yet posted to ERP.
        if (invoice.InvoiceStatus != InvoiceStatus.Submitted || invoice.ErpPostedAt.HasValue)
            throw new ConflictException(
                $"Only a Submitted, not-yet-posted invoice can be revoked; current state '{invoice.InvoiceStatus}'" +
                (invoice.ErpPostedAt.HasValue ? " (already posted to ERP)." : "."));

        // Apply the client's RowVersion as the concurrency token so a stale revoke (vs. a racing auto-post) fails 409.
        if (!string.IsNullOrWhiteSpace(request.Body.RowVersion))
        {
            try { invoice.RowVersion = Convert.FromBase64String(request.Body.RowVersion); }
            catch (FormatException) { throw new ConflictException("Invalid RowVersion; reload the invoice and retry."); }
        }

        var now = DateTime.UtcNow;
        invoice.InvoiceStatus = InvoiceStatus.Draft;
        invoice.RevokedBy = _user.UserCode;
        invoice.RevokedAt = now;
        invoice.RevokeReason = string.IsNullOrWhiteSpace(request.Body.Reason) ? null : request.Body.Reason!.Trim();
        // Clear the prior submit stamp so the Draft is clean for re-submit.
        invoice.SubmittedAt = null;
        invoice.SubmittedBy = null;
        invoice.UpdatedBy = _user.UserCode;
        invoice.UpdatedOn = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent change (e.g. the GRN-gated auto-post) won the race — surface as 409 (no silent overwrite).
            throw new ConflictException("The invoice was modified concurrently (possibly posted). Reload and retry.");
        }

        return await _mediator.Send(new GetInvoiceByIdQuery(invoice.Id), ct);
    }
}
