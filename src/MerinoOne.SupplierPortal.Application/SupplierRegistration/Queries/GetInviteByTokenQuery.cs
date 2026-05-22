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

        var i = await _db.SupplierInvites.FirstOrDefaultAsync(x => x.Token == request.Token, ct)
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
