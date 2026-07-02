using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

/// <summary>
/// Lists Tax master rows visible to the active company. Tax is <c>ICompanyScoped</c> (sharing-aware), so the
/// always-on company filter resolves the shared source company automatically — no manual scoping here (mirrors
/// <c>GetDeliveryTermsQuery</c>).
/// </summary>
public record GetTaxesQuery(bool? IsActive = null) : IRequest<List<TaxDto>>;

public class GetTaxesQueryHandler : IRequestHandler<GetTaxesQuery, List<TaxDto>>
{
    private readonly IAppDbContext _db;
    public GetTaxesQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<TaxDto>> Handle(GetTaxesQuery request, CancellationToken ct)
    {
        var q = _db.Taxes.AsQueryable();
        if (request.IsActive.HasValue)
            q = q.Where(t => t.IsActive == request.IsActive.Value);

        return await q.OrderBy(t => t.Code)
            .Select(t => new TaxDto(t.Id, t.Seq, t.Code, t.Description, t.TaxRate, t.IsActive, t.CreatedOn,
                t.IsRateOverridden, t.LastSyncedRate))
            .ToListAsync(ct);
    }
}
