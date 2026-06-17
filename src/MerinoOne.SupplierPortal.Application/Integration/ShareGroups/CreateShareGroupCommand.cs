using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Integration.ShareGroups;

/// <summary>
/// Tenant-Admin: create an endpoint-wise share group (source company + initial members) for the caller's
/// tenant. Validates the endpoint name, that the source + every member resolve to a company in the tenant,
/// and that no member is already in another group for the same endpoint (the
/// <c>UQ_CompanyShareGroupMember_endpoint_member</c> guard) — surfacing a clean ConflictException rather
/// than a raw DB error.
/// </summary>
/// <remarks>
/// RETAG CAVEAT: this only governs how master data is resolved going forward. Changing a group's
/// source/members is NOT retroactive — rows already stored under the previous source keep their old source
/// company. A separate re-tag job (future task) is required to move already-flowed master data; this
/// command does not block on or perform any re-tagging.
/// </remarks>
public record CreateShareGroupCommand(CreateShareGroupRequest Body) : IRequest<Guid>;

public class CreateShareGroupCommandValidator : AbstractValidator<CreateShareGroupCommand>
{
    public CreateShareGroupCommandValidator()
    {
        RuleFor(x => x.Body.Endpoint).NotEmpty()
            .Must(e => Enum.TryParse<SharedEndpoint>(e, ignoreCase: true, out _))
            .WithMessage($"Endpoint must be one of: {string.Join(", ", Enum.GetNames<SharedEndpoint>())}.");
        RuleFor(x => x.Body.SourceTenantEntityId).NotEmpty()
            .WithMessage("A source company (SourceTenantEntityId) is required.");
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.MemberTenantEntityIds).NotNull();
        RuleForEach(x => x.Body.MemberTenantEntityIds).NotEmpty()
            .WithMessage("Member company ids must be non-empty GUIDs.");
    }
}

public class CreateShareGroupCommandHandler : IRequestHandler<CreateShareGroupCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateShareGroupCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Guid> Handle(CreateShareGroupCommand request, CancellationToken ct)
    {
        var tenantId = _user.TenantId;
        var endpoint = Enum.Parse<SharedEndpoint>(request.Body.Endpoint, ignoreCase: true);
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        // De-dup the requested members; the source company is implicitly part of its own group.
        var memberIds = request.Body.MemberTenantEntityIds.Distinct().ToList();

        // === Resolve + tenant-restrict every company id (source + members) in one pass ===============
        var allCompanyIds = memberIds.Append(request.Body.SourceTenantEntityId).Distinct().ToList();
        var companies = await _db.TenantEntities.IgnoreQueryFilters()
            .Where(e => !e.IsDeleted && allCompanyIds.Contains(e.Id))
            .Where(e => e.TenantId == tenantId)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (!companies.Contains(request.Body.SourceTenantEntityId))
            throw new NotFoundException("Company", request.Body.SourceTenantEntityId);

        var missingMembers = memberIds.Where(id => !companies.Contains(id)).ToList();
        if (missingMembers.Count > 0)
            throw new NotFoundException("Company", missingMembers[0]);

        // === Uniqueness: one group per (tenant, endpoint, source) ====================================
        var sourceClash = await _db.CompanyShareGroups.IgnoreQueryFilters()
            .AnyAsync(g => !g.IsDeleted && g.TenantId == tenantId
                          && g.Endpoint == endpoint && g.SourceTenantEntityId == request.Body.SourceTenantEntityId, ct);
        if (sourceClash)
            throw new ConflictException(
                $"A '{endpoint}' share group already exists for the selected source company. Edit that group instead.");

        // === Uniqueness: a company is a member of at most one group per endpoint ======================
        // (UQ_CompanyShareGroupMember_endpoint_member, filtered on isDeleted=0). Reject the whole create
        // with a clean error listing the offending companies rather than letting the DB throw.
        if (memberIds.Count > 0)
        {
            var taken = await _db.CompanyShareGroupMembers.IgnoreQueryFilters()
                .Where(m => !m.IsDeleted && m.TenantId == tenantId
                           && m.Endpoint == endpoint && memberIds.Contains(m.MemberTenantEntityId))
                .Select(m => m.MemberTenantEntityId)
                .Distinct()
                .ToListAsync(ct);

            if (taken.Count > 0)
            {
                var codes = await _db.TenantEntities.IgnoreQueryFilters()
                    .Where(e => taken.Contains(e.Id))
                    .Select(e => e.Code)
                    .ToListAsync(ct);
                throw new ConflictException(
                    $"These companies are already in another '{endpoint}' share group: {string.Join(", ", codes)}. " +
                    "A company can belong to at most one group per endpoint.");
            }
        }

        var groupId = Guid.NewGuid();
        _db.CompanyShareGroups.Add(new CompanyShareGroup
        {
            Id = groupId,
            TenantId = tenantId,                       // explicit — don't rely on the interceptor
            Endpoint = endpoint,
            SourceTenantEntityId = request.Body.SourceTenantEntityId,
            Name = request.Body.Name.Trim(),
            IsEnabled = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        foreach (var memberId in memberIds)
        {
            _db.CompanyShareGroupMembers.Add(new CompanyShareGroupMember
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,                   // explicit
                CompanyShareGroupId = groupId,
                MemberTenantEntityId = memberId,
                Endpoint = endpoint,                   // denormalized from the group
                CreatedBy = actor,
                CreatedOn = now
            });
        }

        await _db.SaveChangesAsync(ct);
        return groupId;
    }
}
