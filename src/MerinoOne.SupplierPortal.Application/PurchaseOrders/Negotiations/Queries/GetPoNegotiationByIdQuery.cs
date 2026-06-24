using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Queries;

/// <summary>
/// Full negotiation detail for the buyer diff view: header + delta lines (each carrying the original → negotiated
/// values). Lines are loaded via the parent (<c>Include(n => n.Lines)</c>) — there is no root DbSet for lines.
/// Mirrors <c>GetSupplierChangeRequestByIdQuery</c>: reviewers open any negotiation in their tenant (bypass
/// seccode/company, re-apply tenant); suppliers stay seccode-scoped (404 on someone else's negotiation).
/// </summary>
public record GetPoNegotiationByIdQuery(Guid Id) : IRequest<PoNegotiationDto>;

public class GetPoNegotiationByIdQueryHandler : IRequestHandler<GetPoNegotiationByIdQuery, PoNegotiationDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetPoNegotiationByIdQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<PoNegotiationDto> Handle(GetPoNegotiationByIdQuery request, CancellationToken ct)
    {
        var reviewer = _user.IsAdmin || _user.IsManager || _user.HasPermission("PurchaseOrder.ApproveNegotiation");
        var negs = reviewer
            ? _db.PurchaseOrderNegotiations.IgnoreQueryFilters().Where(x => !x.IsDeleted && x.TenantId == _user.TenantId)
            : _db.PurchaseOrderNegotiations.AsQueryable();
        var supSet = reviewer
            ? _db.Suppliers.IgnoreQueryFilters().Where(s => !s.IsDeleted && s.TenantId == _user.TenantId)
            : _db.Suppliers.AsQueryable();

        var n = await negs
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("PurchaseOrderNegotiation", request.Id);

        var supplierName = await supSet
            .Where(s => s.Id == n.SupplierId)
            .Select(s => s.LegalName)
            .FirstOrDefaultAsync(ct);

        return PoNegotiationMapper.ToDto(n, supplierName ?? string.Empty);
    }
}
