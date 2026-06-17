using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Integration.ShareGroups;

/// <summary>
/// Tenant-Admin: add a company to a share group as a member. Validates the company resolves to the caller's
/// tenant and is not already in another group for the same endpoint
/// (<c>UQ_CompanyShareGroupMember_endpoint_member</c>, filtered on isDeleted=0). A previously soft-deleted
/// membership row for the same (group, member) is RESTORED rather than inserting a duplicate (so the
/// <c>UQ_CompanyShareGroupMember_group_member</c> guard is respected).
/// </summary>
/// <remarks>
/// RETAG CAVEAT: adding a member only changes how master data resolves going forward. Rows already stored
/// before this member joined are NOT re-tagged to the group's source; a separate re-tag job (future task)
/// handles already-flowed master data. This command neither blocks on nor performs any re-tagging.
/// </remarks>
public record AddShareGroupMemberCommand(Guid GroupId, AddShareGroupMemberRequest Body) : IRequest<Unit>;

public class AddShareGroupMemberCommandValidator : AbstractValidator<AddShareGroupMemberCommand>
{
    public AddShareGroupMemberCommandValidator()
    {
        RuleFor(x => x.GroupId).NotEmpty();
        RuleFor(x => x.Body.TenantEntityId).NotEmpty()
            .WithMessage("A company (TenantEntityId) is required.");
    }
}

public class AddShareGroupMemberCommandHandler : IRequestHandler<AddShareGroupMemberCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public AddShareGroupMemberCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(AddShareGroupMemberCommand request, CancellationToken ct)
    {
        var tenantId = _user.TenantId;
        var companyId = request.Body.TenantEntityId;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        var group = await _db.CompanyShareGroups.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => !g.IsDeleted && g.TenantId == tenantId && g.Id == request.GroupId, ct)
            ?? throw new NotFoundException("CompanyShareGroup", request.GroupId);

        // Company must resolve to the caller's tenant.
        var companyExists = await _db.TenantEntities.IgnoreQueryFilters()
            .AnyAsync(e => !e.IsDeleted && e.Id == companyId && e.TenantId == tenantId, ct);
        if (!companyExists)
            throw new NotFoundException("Company", companyId);

        // A company belongs to at most one LIVE group per endpoint. If it's already a live member of a
        // DIFFERENT group on this endpoint → clean conflict (would violate the filtered unique index).
        var liveElsewhere = await _db.CompanyShareGroupMembers.IgnoreQueryFilters()
            .AnyAsync(m => !m.IsDeleted && m.TenantId == tenantId
                          && m.Endpoint == group.Endpoint && m.MemberTenantEntityId == companyId
                          && m.CompanyShareGroupId != group.Id, ct);
        if (liveElsewhere)
        {
            var code = await _db.TenantEntities.IgnoreQueryFilters()
                .Where(e => e.Id == companyId).Select(e => e.Code).FirstOrDefaultAsync(ct);
            throw new ConflictException(
                $"Company '{code}' is already in another '{group.Endpoint}' share group. " +
                "A company can belong to at most one group per endpoint.");
        }

        // Existing membership row for THIS (group, member) — including soft-deleted. An active row is a
        // no-op-conflict; a soft-deleted row is restored instead of inserting a duplicate
        // (UQ_CompanyShareGroupMember_group_member is NOT filtered, so a duplicate would clash).
        var existing = await _db.CompanyShareGroupMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.CompanyShareGroupId == group.Id && m.MemberTenantEntityId == companyId, ct);

        if (existing is not null && !existing.IsDeleted)
            throw new ConflictException(
                $"Company is already a member of this '{group.Endpoint}' share group.");

        if (existing is not null)
        {
            // Restore the soft-deleted membership.
            existing.IsDeleted = false;
            existing.DeletedBy = null;
            existing.DeletedOn = null;
            existing.TenantId = tenantId;
            existing.Endpoint = group.Endpoint;
            existing.UpdatedBy = actor;
            existing.UpdatedOn = now;
        }
        else
        {
            _db.CompanyShareGroupMembers.Add(new CompanyShareGroupMember
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,                   // explicit
                CompanyShareGroupId = group.Id,
                MemberTenantEntityId = companyId,
                Endpoint = group.Endpoint,             // denormalized from the group
                CreatedBy = actor,
                CreatedOn = now
            });
        }

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
