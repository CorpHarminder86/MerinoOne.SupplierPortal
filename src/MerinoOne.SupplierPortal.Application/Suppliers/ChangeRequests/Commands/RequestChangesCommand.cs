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
/// Reviewer bounces a change request back to the supplier for amendment:
/// <see cref="ChangeRequestStatus.Submitted"/> / <see cref="ChangeRequestStatus.UnderReview"/> →
/// <see cref="ChangeRequestStatus.ChangesRequested"/>. The reason is recorded; the supplier may then edit + resubmit.
/// Gated on <c>Supplier.ApproveChange</c> at the controller; internal reviewers see all requests (no seccode write
/// gate — they are privileged on the supplier seccode).
/// </summary>
public record RequestChangesCommand(Guid Id, RequestChangesRequest Body) : IRequest<Unit>;

public class RequestChangesCommandValidator : AbstractValidator<RequestChangesCommand>
{
    public RequestChangesCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Reason).NotEmpty().MaximumLength(1000)
            .WithMessage("A reason is required when requesting changes.");
    }
}

public class RequestChangesCommandHandler : IRequestHandler<RequestChangesCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public RequestChangesCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<Unit> Handle(RequestChangesCommand request, CancellationToken ct)
    {
        var entity = await _db.SupplierChangeRequests.FirstOrDefaultAsync(r => r.Id == request.Id, ct)
                     ?? throw new NotFoundException("SupplierChangeRequest", request.Id);

        if (entity.ChangeStatus is not (ChangeRequestStatus.Submitted or ChangeRequestStatus.UnderReview))
            throw new ConflictException($"Changes can only be requested on a Submitted or UnderReview request (current: {entity.ChangeStatus}).");

        var now = DateTime.UtcNow;
        entity.ChangeStatus = ChangeRequestStatus.ChangesRequested;
        entity.ReviewedBy = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
        entity.ReviewedAt = now;
        entity.RejectionReason = request.Body.Reason.Trim();   // reused column carries the "changes requested" note.
        entity.UpdatedBy = entity.ReviewedBy;
        entity.UpdatedOn = now;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
