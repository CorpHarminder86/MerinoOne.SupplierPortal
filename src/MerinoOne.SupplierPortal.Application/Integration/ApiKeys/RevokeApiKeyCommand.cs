using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Integration.ApiKeys;

/// <summary>
/// Tenant-Admin: revoke a key. Sets IsActive = false + RevokedAt so the auth handler rejects it
/// immediately (lookup is WHERE isActive = 1). Idempotent — re-revoking an already-revoked key is a no-op.
/// </summary>
public record RevokeApiKeyCommand(Guid Id) : IRequest<Unit>;

public class RevokeApiKeyCommandHandler : IRequestHandler<RevokeApiKeyCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public RevokeApiKeyCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(RevokeApiKeyCommand request, CancellationToken ct)
    {
        // Tenant-filtered — a Tenant Admin can only revoke its own tenant's keys.
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == request.Id, ct)
            ?? throw new NotFoundException("ApiKey", request.Id);

        if (!key.IsActive && key.RevokedAt.HasValue)
            return Unit.Value;

        var now = DateTime.UtcNow;
        key.IsActive = false;
        key.RevokedAt = now;
        key.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        key.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
