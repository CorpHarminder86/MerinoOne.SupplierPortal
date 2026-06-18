using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Reads the current tenant's <c>InforConnectionSetting</c> row and returns it with the secret
/// columns decrypted via <see cref="ISettingProtector"/>. Scoped — resolves the per-request tenant
/// from <see cref="ICurrentUser"/> and queries explicitly by tenant so it is correct regardless of
/// the gated global tenant filter.
/// </summary>
public class InforConnectionProvider : IInforConnectionProvider
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISettingProtector _protector;
    private readonly ILogger<InforConnectionProvider> _logger;

    public InforConnectionProvider(
        IAppDbContext db,
        ICurrentUser user,
        ISettingProtector protector,
        ILogger<InforConnectionProvider> logger)
    {
        _db = db;
        _user = user;
        _protector = protector;
        _logger = logger;
    }

    public async Task<InforConnectionValues?> GetCurrentAsync(CancellationToken ct = default)
    {
        if (_user.TenantId is not { } tenantId) return null;

        var row = await _db.InforConnectionSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        if (row is null) return null;

        return new InforConnectionValues(
            AccessTokenUrl: row.AccessTokenUrl,
            ClientId: row.ClientId,
            ClientSecret: Decrypt(row.ClientSecret),
            Username: row.Username,
            Password: Decrypt(row.Password),
            ApiBaseUrl: row.ApiBaseUrl,
            IonC4wsBaseUrl: row.IonC4wsBaseUrl,
            Company: row.Company,
            IsActive: row.IsActive);
    }

    private string Decrypt(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return string.Empty;
        try { return _protector.Unprotect(cipher); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Infor secret could not be unprotected (key-ring rotation?).");
            return string.Empty;
        }
    }
}
