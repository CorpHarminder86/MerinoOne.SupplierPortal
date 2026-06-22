using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Commands;

/// <summary>
/// Supplier edits the line-set + summary of a change request. Permitted ONLY while the request is
/// <see cref="ChangeRequestStatus.Draft"/> or <see cref="ChangeRequestStatus.ChangesRequested"/> (the two
/// supplier-editable states). Replaces the editable lines wholesale (the old lines are soft-deleted and the new
/// set inserted) — simplest correct semantics for a delta envelope that hasn't been applied yet. canWrite-gated
/// via <see cref="SupplierWriteGuard"/> (the supplier permission-based path).
/// </summary>
public record UpdateSupplierChangeRequestCommand(Guid Id, UpdateSupplierChangeRequestRequest Body)
    : IRequest<SupplierChangeRequestDto>;

public class UpdateSupplierChangeRequestCommandValidator : AbstractValidator<UpdateSupplierChangeRequestCommand>
{
    public UpdateSupplierChangeRequestCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Summary).MaximumLength(500);
        RuleFor(x => x.Body.Lines).Custom((lines, ctx) =>
        {
            if (lines is null) return;
            for (var i = 0; i < lines.Count; i++)
                foreach (var err in SupplierChangeLineRules.Validate(lines[i], i))
                    ctx.AddFailure($"Lines[{i}]", err);
        });
    }
}

public class UpdateSupplierChangeRequestCommandHandler : IRequestHandler<UpdateSupplierChangeRequestCommand, SupplierChangeRequestDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public UpdateSupplierChangeRequestCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<SupplierChangeRequestDto> Handle(UpdateSupplierChangeRequestCommand request, CancellationToken ct)
    {
        var entity = await _db.SupplierChangeRequests
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == request.Id, ct)
            ?? throw new NotFoundException("SupplierChangeRequest", request.Id);

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == entity.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", entity.SupplierId);
        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        if (entity.ChangeStatus is not (ChangeRequestStatus.Draft or ChangeRequestStatus.ChangesRequested))
            throw new ConflictException($"A change request can only be edited while Draft or ChangesRequested (current: {entity.ChangeStatus}).");

        var now = DateTime.UtcNow;
        entity.Summary = string.IsNullOrWhiteSpace(request.Body.Summary) ? null : request.Body.Summary.Trim();

        // Replace the line-set: soft-delete the existing (not-yet-applied) lines, insert the new set.
        foreach (var existing in entity.Lines.Where(l => !l.IsDeleted).ToList())
            _db.SupplierChangeRequestLines.Remove(existing);

        foreach (var input in request.Body.Lines ?? new List<SupplierChangeLineInput>())
            entity.Lines.Add(SupplierChangeRequestMapper.BuildLine(input, Actor(), now));

        entity.UpdatedBy = Actor();
        entity.UpdatedOn = now;
        await _db.SaveChangesAsync(ct);

        return SupplierChangeRequestMapper.ToDto(entity, supplier.SupplierCode, supplier.LegalName);
    }

    private string Actor() => string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
}
