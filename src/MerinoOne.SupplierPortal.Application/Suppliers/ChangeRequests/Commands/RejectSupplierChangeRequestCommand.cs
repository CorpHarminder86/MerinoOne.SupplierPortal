using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Commands;

/// <summary>
/// Reviewer rejects a change request: <see cref="ChangeRequestStatus.Submitted"/> /
/// <see cref="ChangeRequestStatus.UnderReview"/> → <see cref="ChangeRequestStatus.Rejected"/>. The reason is
/// required. No deltas are applied and nothing is pushed to ERP. Gated on <c>Supplier.ApproveChange</c>.
/// </summary>
public record RejectSupplierChangeRequestCommand(Guid Id, RejectSupplierChangeRequest Body) : IRequest<Unit>;

public class RejectSupplierChangeRequestCommandValidator : AbstractValidator<RejectSupplierChangeRequestCommand>
{
    public RejectSupplierChangeRequestCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Reason).NotEmpty().MaximumLength(1000)
            .WithMessage("A rejection reason is required.");
    }
}

public class RejectSupplierChangeRequestCommandHandler : IRequestHandler<RejectSupplierChangeRequestCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public RejectSupplierChangeRequestCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<Unit> Handle(RejectSupplierChangeRequestCommand request, CancellationToken ct)
    {
        var entity = await _db.SupplierChangeRequests.FirstOrDefaultAsync(r => r.Id == request.Id, ct)
                     ?? throw new NotFoundException("SupplierChangeRequest", request.Id);

        if (entity.ChangeStatus is not (ChangeRequestStatus.Submitted or ChangeRequestStatus.UnderReview))
            throw new ConflictException($"Only a Submitted or UnderReview request can be rejected (current: {entity.ChangeStatus}).");

        var now = DateTime.UtcNow;
        entity.ChangeStatus = ChangeRequestStatus.Rejected;
        entity.ReviewedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
        entity.ReviewedAt = now;
        entity.RejectionReason = request.Body.Reason.Trim();
        entity.UpdatedBy = entity.ReviewedBy;
        entity.UpdatedOn = now;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
