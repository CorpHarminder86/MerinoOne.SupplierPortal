using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Queries;

/// <summary>
/// Full change-request detail for the diff view: header + lines (each carrying the old→new diff for Edit, the
/// payloadJson for Add, the target id for Delete) + per-line push state (PushStatus / PushedAt / ErpRef). Lines
/// are loaded via the parent (<c>Include(r => r.Lines)</c>) — there is no root DbSet for lines. Seccode-scoped:
/// a supplier sees only its own request (404 otherwise); internal users see all.
/// </summary>
public record GetSupplierChangeRequestByIdQuery(Guid Id) : IRequest<SupplierChangeRequestDto>;

public class GetSupplierChangeRequestByIdQueryHandler
    : IRequestHandler<GetSupplierChangeRequestByIdQuery, SupplierChangeRequestDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetSupplierChangeRequestByIdQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<SupplierChangeRequestDto> Handle(GetSupplierChangeRequestByIdQuery request, CancellationToken ct)
    {
        // Reviewers open any request in their tenant (the queue spans companies); bypass seccode/company but
        // re-apply the tenant predicate. Suppliers stay seccode-scoped (404 on someone else's request).
        var reviewer = _user.IsAdmin || _user.IsManager || _user.HasPermission(Perm.SupplierApproveChange);
        var reqs = reviewer
            ? _db.SupplierChangeRequests.IgnoreQueryFilters().Where(x => !x.IsDeleted && x.TenantId == _user.TenantId)
            : _db.SupplierChangeRequests.AsQueryable();
        var supSet = reviewer
            ? _db.Suppliers.IgnoreQueryFilters().Where(s => !s.IsDeleted && s.TenantId == _user.TenantId)
            : _db.Suppliers.AsQueryable();

        var r = await reqs
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("SupplierChangeRequest", request.Id);

        var supplier = await supSet
            .Select(s => new { s.Id, s.SupplierCode, s.LegalName })
            .FirstOrDefaultAsync(s => s.Id == r.SupplierId, ct);

        return SupplierChangeRequestMapper.ToDto(
            r,
            supplier?.SupplierCode ?? string.Empty,
            supplier?.LegalName ?? string.Empty);
    }
}
