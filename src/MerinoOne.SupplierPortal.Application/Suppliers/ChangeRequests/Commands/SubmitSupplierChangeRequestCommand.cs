using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Commands;

/// <summary>
/// Supplier submits a change request for review: <see cref="ChangeRequestStatus.Draft"/> or
/// <see cref="ChangeRequestStatus.ChangesRequested"/> → <see cref="ChangeRequestStatus.Submitted"/>. Requires
/// at least one (non-deleted) line — the ≥1-line rule is enforced here (a Draft may be created empty).
/// canWrite-gated via <see cref="SupplierWriteGuard"/> (the supplier permission-based path).
/// </summary>
public record SubmitSupplierChangeRequestCommand(Guid Id) : IRequest<Unit>;

public class SubmitSupplierChangeRequestCommandHandler : IRequestHandler<SubmitSupplierChangeRequestCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public SubmitSupplierChangeRequestCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<Unit> Handle(SubmitSupplierChangeRequestCommand request, CancellationToken ct)
    {
        var entity = await _db.SupplierChangeRequests
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == request.Id, ct)
            ?? throw new NotFoundException("SupplierChangeRequest", request.Id);

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == entity.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", entity.SupplierId);
        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        if (entity.ChangeStatus is not (ChangeRequestStatus.Draft or ChangeRequestStatus.ChangesRequested))
            throw new ConflictException($"Only a Draft or ChangesRequested change request can be submitted (current: {entity.ChangeStatus}).");

        if (!entity.Lines.Any(l => !l.IsDeleted))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "A change request must contain at least one change before it can be submitted." }
            });

        entity.ChangeStatus = ChangeRequestStatus.Submitted;
        entity.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
        entity.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
