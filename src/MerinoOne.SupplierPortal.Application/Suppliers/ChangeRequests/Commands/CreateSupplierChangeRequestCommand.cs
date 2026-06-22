using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Commands;

/// <summary>
/// A supplier raises a post-registration change request (Module 2). The request is built from the proposed deltas
/// (one <see cref="SupplierChangeRequestLine"/> per change) and NEVER mutates live supplier data — the deltas are
/// applied only on approve. The request starts in <see cref="ChangeRequestStatus.Draft"/>.
///
/// <para><b>Supplier write path (plan §4, security-sensitive — call out for review):</b> a supplier user has
/// <c>SecRight.canWrite=false</c> on its G-seccode (suppliers are ERP-read-only on their own master). We therefore
/// do NOT rely on the row-level write grant. Authorization runs through <see cref="SupplierWriteGuard"/>, which —
/// for a supplier principal — requires the <c>Supplier.ChangeRequest</c> permission AND a verified
/// <c>SupplierUserMap</c> membership for the target supplier, and permits writing THIS portal-originated aggregate
/// (the change request) WITHOUT toggling the global <c>canWrite</c> grant. The global RLS write rule is not weakened:
/// the supplier still cannot write the live supplier/address/contact/bank/license rows directly — only this
/// change-request envelope, whose Owner is stamped to the supplier's G-seccode so seccode RLS still scopes it.</para>
/// </summary>
public record CreateSupplierChangeRequestCommand(CreateSupplierChangeRequestRequest Body) : IRequest<SupplierChangeRequestDto>;

public class CreateSupplierChangeRequestCommandValidator : AbstractValidator<CreateSupplierChangeRequestCommand>
{
    public CreateSupplierChangeRequestCommandValidator()
    {
        RuleFor(x => x.Body.SupplierId).NotEmpty();
        RuleFor(x => x.Body.Summary).MaximumLength(500);
        // A Draft may be created empty (the supplier fills lines as they go) — the ≥1-line rule is enforced on Submit.
        // But any line PRESENT at create must be well-formed (per-target rules, reusing module 1).
        RuleFor(x => x.Body.Lines).Custom((lines, ctx) =>
        {
            if (lines is null) return;
            for (var i = 0; i < lines.Count; i++)
                foreach (var err in SupplierChangeLineRules.Validate(lines[i], i))
                    ctx.AddFailure($"Lines[{i}]", err);
        });
    }
}

public class CreateSupplierChangeRequestCommandHandler : IRequestHandler<CreateSupplierChangeRequestCommand, SupplierChangeRequestDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public CreateSupplierChangeRequestCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<SupplierChangeRequestDto> Handle(CreateSupplierChangeRequestCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.Body.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.Body.SupplierId);

        // Permission-based supplier write-path authorization (see XML doc above). Throws ForbiddenException (403).
        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        var now = DateTime.UtcNow;
        var entity = new SupplierChangeRequest
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            ChangeStatus = ChangeRequestStatus.Draft,
            RequestedBy = Actor(),
            RequestedAt = now,
            Summary = string.IsNullOrWhiteSpace(request.Body.Summary) ? null : request.Body.Summary.Trim(),
            SeccodeId = supplier.SeccodeId,   // Owner = supplier's G-seccode (seccode RLS).
            CreatedBy = Actor(),
            CreatedOn = now,
        };

        foreach (var input in request.Body.Lines ?? new List<SupplierChangeLineInput>())
            entity.Lines.Add(SupplierChangeRequestMapper.BuildLine(input, Actor(), now));

        _db.SupplierChangeRequests.Add(entity);
        await _db.SaveChangesAsync(ct);

        return SupplierChangeRequestMapper.ToDto(entity, supplier.SupplierCode, supplier.LegalName);
    }

    private string Actor() => string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
}
