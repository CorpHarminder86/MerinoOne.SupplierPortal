using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

public record GetPaymentTermsQuery(bool? IsActive = null) : IRequest<List<PaymentTermDto>>;

public class GetPaymentTermsQueryHandler : IRequestHandler<GetPaymentTermsQuery, List<PaymentTermDto>>
{
    private readonly IAppDbContext _db;
    public GetPaymentTermsQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<PaymentTermDto>> Handle(GetPaymentTermsQuery request, CancellationToken ct)
    {
        var q = _db.PaymentTerms.AsQueryable();
        if (request.IsActive.HasValue)
            q = q.Where(p => p.IsActive == request.IsActive.Value);

        return await q.OrderBy(p => p.Code)
            .Select(p => new PaymentTermDto(p.Id, p.Seq, p.Code, p.Description, p.NetDays, p.IsActive, p.CreatedOn))
            .ToListAsync(ct);
    }
}
