using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.SupplierRegistration.Queries;

public record GetInvitesListQuery(string? Status = null, string? Search = null) : IRequest<List<SupplierInviteListDto>>;

public class GetInvitesListQueryHandler : IRequestHandler<GetInvitesListQuery, List<SupplierInviteListDto>>
{
    private readonly IAppDbContext _db;
    public GetInvitesListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<SupplierInviteListDto>> Handle(GetInvitesListQuery request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var q = _db.SupplierInvites.AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(i => i.LegalName.Contains(t) || i.Email.Contains(t));
        }

        var rows = await q.OrderByDescending(i => i.InvitedAt).ToListAsync(ct);

        return rows.Select(i =>
        {
            string status = i.ConsumedAt.HasValue
                ? "Consumed"
                : (i.ExpiresAt < now ? "Expired" : "Pending");
            return new SupplierInviteListDto(
                i.Id, i.Seq, i.LegalName, i.Email, i.InvitedBy,
                i.InvitedAt, i.ExpiresAt, i.ConsumedAt, i.SupplierId, i.Token, status);
        })
        .Where(d => string.IsNullOrWhiteSpace(request.Status) ||
                    string.Equals(d.Status, request.Status, StringComparison.OrdinalIgnoreCase))
        .ToList();
    }
}
