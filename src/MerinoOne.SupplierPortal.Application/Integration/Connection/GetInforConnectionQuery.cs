using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Connection;

/// <summary>
/// Returns the current tenant's Infor connection config (or an empty/unconfigured shape when none is
/// saved). Secrets are masked with "********" so the ciphertext never leaves the API. The lookup is
/// pinned to the caller's tenant explicitly (not only via the gated global filter) so it is correct
/// regardless of the Scope.FiltersEnabled rollout state.
/// </summary>
public record GetInforConnectionQuery : IRequest<InforConnectionDto>;

public class GetInforConnectionQueryHandler : IRequestHandler<GetInforConnectionQuery, InforConnectionDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public GetInforConnectionQueryHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<InforConnectionDto> Handle(GetInforConnectionQuery request, CancellationToken ct)
    {
        var tenantId = _user.TenantId;

        var row = tenantId is null
            ? null
            : await _db.InforConnectionSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        if (row is null)
        {
            return new InforConnectionDto(
                AccessTokenUrl: string.Empty,
                ClientId: string.Empty,
                ClientSecret: string.Empty,
                Username: string.Empty,
                Password: string.Empty,
                ApiBaseUrl: string.Empty,
                IonC4wsBaseUrl: null,
                Company: string.Empty,
                IsActive: true,
                IsConfigured: false);
        }

        // Mask secrets — surface the sentinel only when a value is actually stored.
        var maskedSecret = string.IsNullOrEmpty(row.ClientSecret) ? string.Empty : InforConnectionSecret.Mask;
        var maskedPassword = string.IsNullOrEmpty(row.Password) ? string.Empty : InforConnectionSecret.Mask;

        var isConfigured = !string.IsNullOrWhiteSpace(row.AccessTokenUrl)
                           && !string.IsNullOrWhiteSpace(row.ApiBaseUrl)
                           && !string.IsNullOrWhiteSpace(row.Company);

        return new InforConnectionDto(
            AccessTokenUrl: row.AccessTokenUrl,
            ClientId: row.ClientId,
            ClientSecret: maskedSecret,
            Username: row.Username,
            Password: maskedPassword,
            ApiBaseUrl: row.ApiBaseUrl,
            IonC4wsBaseUrl: row.IonC4wsBaseUrl,
            Company: row.Company,
            IsActive: row.IsActive,
            IsConfigured: isConfigured);
    }
}
