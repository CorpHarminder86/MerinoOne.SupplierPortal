using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.SupplierRegistration.Queries;

public record GetInviteByTokenQuery(string Token) : IRequest<SupplierInviteDetailDto>;

public class GetInviteByTokenQueryHandler : IRequestHandler<GetInviteByTokenQuery, SupplierInviteDetailDto>
{
    private readonly IAppDbContext _db;
    public GetInviteByTokenQueryHandler(IAppDbContext db) => _db = db;

    public async Task<SupplierInviteDetailDto> Handle(GetInviteByTokenQuery request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new NotFoundException("SupplierInvite", request.Token);

        // Anonymous /register/{token} caller has CurrentTenantId == null and is NOT bypassed by the
        // always-on tenant filter, so the (valid, tenant-tagged) invite is hidden. IgnoreQueryFilters()
        // bypasses BOTH the tenant filter AND soft-delete; re-apply !IsDeleted manually. Token remains
        // the security boundary; all business checks below are preserved. (Mirrors AuthController.Login.)
        var i = await _db.SupplierInvites.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Token == request.Token && !x.IsDeleted, ct)
                ?? throw new NotFoundException("SupplierInvite", request.Token);

        var now = DateTime.UtcNow;
        if (i.ConsumedAt.HasValue)
            throw new ConflictException("This invite has already been used.");
        if (i.ExpiresAt < now)
            throw new ConflictException("This invite has expired.");

        return new SupplierInviteDetailDto(
            i.Id, i.Seq, i.LegalName, i.Email, i.InvitedBy,
            i.InvitedAt, i.ExpiresAt, i.ConsumedAt, i.SupplierId, i.Token, "Pending");
    }
}
