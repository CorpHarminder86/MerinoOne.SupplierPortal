using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Mdm;

// Slim typeahead lookups for the address geo cascade. TenantId is supplied EXPLICITLY for the anonymous
// (onboarding) path — the request has no principal — and queried with IgnoreQueryFilters + an explicit
// tenant predicate. When TenantId is null (authenticated) the ambient tenant filter applies.

public static class GeoLookup
{
    /// <summary>Page size for the typeahead lookups; the autocomplete pages with <c>skip</c> in multiples of this.</summary>
    public const int Cap = 20;
    private static int Page(int skip) => skip < 0 ? 0 : skip;
    internal static int Skip(int skip) => Page(skip);
}

public record GeoCountryLookupQuery(string? Search, Guid? TenantId = null, int Skip = 0) : IRequest<List<GeoLookupDto>>;
public class GeoCountryLookupQueryHandler(IAppDbContext db) : IRequestHandler<GeoCountryLookupQuery, List<GeoLookupDto>>
{
    public async Task<List<GeoLookupDto>> Handle(GeoCountryLookupQuery request, CancellationToken ct)
    {
        var q = request.TenantId is Guid tid
            ? db.Countries.IgnoreQueryFilters().Where(x => x.TenantId == tid && !x.IsDeleted)
            : db.Countries.AsQueryable();
        q = q.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Description).ThenBy(x => x.Id)
            .Skip(GeoLookup.Skip(request.Skip)).Take(GeoLookup.Cap)
            .Select(x => new GeoLookupDto(x.Id, x.Code, x.Description)).ToListAsync(ct);
    }
}

public record GeoStateLookupQuery(Guid CountryId, string? Search, Guid? TenantId = null, int Skip = 0) : IRequest<List<GeoLookupDto>>;
public class GeoStateLookupQueryHandler(IAppDbContext db) : IRequestHandler<GeoStateLookupQuery, List<GeoLookupDto>>
{
    public async Task<List<GeoLookupDto>> Handle(GeoStateLookupQuery request, CancellationToken ct)
    {
        var q = request.TenantId is Guid tid
            ? db.States.IgnoreQueryFilters().Where(x => x.TenantId == tid && !x.IsDeleted)
            : db.States.AsQueryable();
        q = q.Where(x => x.IsActive && x.CountryId == request.CountryId);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Description).ThenBy(x => x.Id)
            .Skip(GeoLookup.Skip(request.Skip)).Take(GeoLookup.Cap)
            .Select(x => new GeoLookupDto(x.Id, x.Code, x.Description)).ToListAsync(ct);
    }
}

public record GeoCityLookupQuery(Guid? CountryId, Guid? StateId, string? Search, Guid? TenantId = null, int Skip = 0) : IRequest<List<GeoLookupDto>>;
public class GeoCityLookupQueryHandler(IAppDbContext db) : IRequestHandler<GeoCityLookupQuery, List<GeoLookupDto>>
{
    public async Task<List<GeoLookupDto>> Handle(GeoCityLookupQuery request, CancellationToken ct)
    {
        var q = request.TenantId is Guid tid
            ? db.Cities.IgnoreQueryFilters().Where(x => x.TenantId == tid && !x.IsDeleted)
            : db.Cities.AsQueryable();
        q = q.Where(x => x.IsActive);
        if (request.StateId.HasValue) q = q.Where(x => x.StateId == request.StateId.Value);
        if (request.CountryId.HasValue) q = q.Where(x => x.CountryId == request.CountryId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Description).ThenBy(x => x.Id)
            .Skip(GeoLookup.Skip(request.Skip)).Take(GeoLookup.Cap)
            .Select(x => new GeoLookupDto(x.Id, x.Code, x.Description)).ToListAsync(ct);
    }
}

// PostalCode search is filtered by CountryId only (NOT state/city). The PIN is the source of truth that
// REVERSE-fills state/city, so constraining it by an already-filled state/city would over-narrow the next
// search and make the typeahead appear to "stop working" after a selection.
public record GeoPostalCodeLookupQuery(Guid? CountryId, Guid? StateId, Guid? CityId, string? Search, Guid? TenantId = null, int Skip = 0) : IRequest<List<GeoLookupDto>>;
public class GeoPostalCodeLookupQueryHandler(IAppDbContext db) : IRequestHandler<GeoPostalCodeLookupQuery, List<GeoLookupDto>>
{
    public async Task<List<GeoLookupDto>> Handle(GeoPostalCodeLookupQuery request, CancellationToken ct)
    {
        var q = request.TenantId is Guid tid
            ? db.PostalCodes.IgnoreQueryFilters().Where(x => x.TenantId == tid && !x.IsDeleted)
            : db.PostalCodes.AsQueryable();
        q = q.Where(x => x.IsActive);
        if (request.CountryId.HasValue) q = q.Where(x => x.CountryId == request.CountryId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || (x.Area != null && x.Area.Contains(t)));
        }
        return await q.OrderBy(x => x.Code).ThenBy(x => x.Id)
            .Skip(GeoLookup.Skip(request.Skip)).Take(GeoLookup.Cap)
            .Select(x => new GeoLookupDto(x.Id, x.Code, x.Area ?? x.Code)).ToListAsync(ct);
    }
}

/// <summary>
/// Reverse-cascade autofill source for the onboarding address step: loads a single PostalCode by Id and
/// projects its linked City/State/Country (names = master Descriptions) so the UI can fill area/city/state/
/// country when a PIN is selected. Anonymous path — the <c>Token</c> is validated (same invite gate as the
/// typeahead lookups) and resolved to the tenant the lookup is scoped to, queried with IgnoreQueryFilters +
/// an explicit tenant predicate. Returns null when the code is unknown within the tenant.
/// </summary>
public record GetPublicPostalCodeDetailQuery(string Token, Guid Id) : IRequest<PostalCodeDetailDto?>;
public class GetPublicPostalCodeDetailQueryHandler(IAppDbContext db, ISender sender)
    : IRequestHandler<GetPublicPostalCodeDetailQuery, PostalCodeDetailDto?>
{
    public async Task<PostalCodeDetailDto?> Handle(GetPublicPostalCodeDetailQuery request, CancellationToken ct)
    {
        var tenantId = await sender.Send(new GetInviteTenantQuery(request.Token), ct);
        return await db.PostalCodes.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Id == request.Id)
            .Select(x => new PostalCodeDetailDto(
                x.Id,
                x.Code,
                x.Area,
                x.CityId,
                x.City != null ? x.City.Description : null,
                x.StateId,
                x.State != null ? x.State.Description : null,
                x.CountryId,
                x.Country != null ? x.Country.Description : null))
            .FirstOrDefaultAsync(ct);
    }
}

/// <summary>
/// Resolves the tenant behind a registration invite token for the anonymous geo lookups. Rejects an
/// unknown, expired or already-consumed invite (the same gate the registration page enforces).
/// </summary>
public record GetInviteTenantQuery(string Token) : IRequest<Guid>;
public class GetInviteTenantQueryHandler(IAppDbContext db) : IRequestHandler<GetInviteTenantQuery, Guid>
{
    public async Task<Guid> Handle(GetInviteTenantQuery request, CancellationToken ct)
    {
        var token = (request.Token ?? string.Empty).Trim();
        var inv = await db.SupplierInvites.IgnoreQueryFilters()
            .Where(i => i.Token == token && !i.IsDeleted)
            .Select(i => new { i.TenantId, i.ExpiresAt, i.ConsumedAt })
            .FirstOrDefaultAsync(ct);
        if (inv is null || inv.TenantId is null)
            throw new NotFoundException("Invite", token);
        if (inv.ConsumedAt != null || inv.ExpiresAt < DateTime.UtcNow)
            throw new ForbiddenException("This registration link is no longer valid.");
        return inv.TenantId.Value;
    }
}
