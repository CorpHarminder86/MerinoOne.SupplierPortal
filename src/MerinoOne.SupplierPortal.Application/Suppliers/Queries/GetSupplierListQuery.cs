using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Queries;

public record GetSupplierListQuery(string? Status = null, string? Search = null) : IRequest<List<SupplierListItemDto>>;

public class GetSupplierListQueryHandler : IRequestHandler<GetSupplierListQuery, List<SupplierListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetSupplierListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<SupplierListItemDto>> Handle(GetSupplierListQuery request, CancellationToken ct)
    {
        var q = _db.Suppliers.AsQueryable();
        if (!string.IsNullOrEmpty(request.Status))
            q = q.Where(s => s.RegistrationStatus.ToString() == request.Status);
        if (!string.IsNullOrEmpty(request.Search))
            q = q.Where(s => s.LegalName.Contains(request.Search) || s.SupplierCode.Contains(request.Search));

        return await q
            .OrderBy(s => s.LegalName)
            .Select(s => new SupplierListItemDto(
                s.Id, s.Seq, s.SupplierCode, s.LegalName, s.TradeName,
                s.GstNumber, s.PanNumber, s.RegistrationStatus.ToString(),
                s.IsActiveSupplier, s.CreatedOn))
            .ToListAsync(ct);
    }
}
