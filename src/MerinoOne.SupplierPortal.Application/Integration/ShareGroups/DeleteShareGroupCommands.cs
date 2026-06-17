using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Integration.ShareGroups;

/// <summary>
/// Tenant-Admin: soft-delete a single member of a share group (by the member's company id). The filtered
/// <c>UQ_CompanyShareGroupMember_endpoint_member</c> index ignores soft-deleted rows, so the company is
/// immediately free to join another group on this endpoint. Idempotent: removing an already-gone member
/// is a no-op success.
/// </summary>
public record RemoveShareGroupMemberCommand(Guid GroupId, Guid TenantEntityId) : IRequest<Unit>;

public class RemoveShareGroupMemberCommandValidator : AbstractValidator<RemoveShareGroupMemberCommand>
{
    public RemoveShareGroupMemberCommandValidator()
    {
        RuleFor(x => x.GroupId).NotEmpty();
        RuleFor(x => x.TenantEntityId).NotEmpty();
    }
}

public class RemoveShareGroupMemberCommandHandler : IRequestHandler<RemoveShareGroupMemberCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public RemoveShareGroupMemberCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(RemoveShareGroupMemberCommand request, CancellationToken ct)
    {
        var tenantId = _user.TenantId;

        // Confirm the group is in the caller's tenant (cross-tenant id → 404).
        var group = await _db.CompanyShareGroups.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => !g.IsDeleted && g.TenantId == tenantId && g.Id == request.GroupId, ct)
            ?? throw new NotFoundException("CompanyShareGroup", request.GroupId);

        var member = await _db.CompanyShareGroupMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => !m.IsDeleted && m.TenantId == tenantId
                                     && m.CompanyShareGroupId == group.Id
                                     && m.MemberTenantEntityId == request.TenantEntityId, ct);

        if (member is null)
            return Unit.Value; // already absent — idempotent success

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        member.IsDeleted = true;
        member.DeletedBy = actor;
        member.DeletedOn = now;
        member.UpdatedBy = actor;
        member.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>
/// Tenant-Admin: soft-delete a share group AND all of its live members. Once gone, those companies'
/// master data falls back to per-company storage (no shared source) and the companies are free to be
/// re-grouped on this endpoint. Scoped to the caller's tenant.
/// </summary>
public record DeleteShareGroupCommand(Guid Id) : IRequest<Unit>;

public class DeleteShareGroupCommandValidator : AbstractValidator<DeleteShareGroupCommand>
{
    public DeleteShareGroupCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
    }
}

public class DeleteShareGroupCommandHandler : IRequestHandler<DeleteShareGroupCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public DeleteShareGroupCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(DeleteShareGroupCommand request, CancellationToken ct)
    {
        var tenantId = _user.TenantId;

        var group = await _db.CompanyShareGroups.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => !g.IsDeleted && g.TenantId == tenantId && g.Id == request.Id, ct)
            ?? throw new NotFoundException("CompanyShareGroup", request.Id);

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        var members = await _db.CompanyShareGroupMembers.IgnoreQueryFilters()
            .Where(m => !m.IsDeleted && m.TenantId == tenantId && m.CompanyShareGroupId == group.Id)
            .ToListAsync(ct);

        foreach (var member in members)
        {
            member.IsDeleted = true;
            member.DeletedBy = actor;
            member.DeletedOn = now;
            member.UpdatedBy = actor;
            member.UpdatedOn = now;
        }

        group.IsDeleted = true;
        group.DeletedBy = actor;
        group.DeletedOn = now;
        group.UpdatedBy = actor;
        group.UpdatedOn = now;

        // Single SaveChangesAsync — EF wraps the group + member soft-deletes in one implicit transaction.
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
