using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

// Inbound tenant-scoped reference masters (Currency / Country / State / City / PostalCode). No CompanyCode:
// the tenant comes from the API key. Parents are referenced by CODE and resolved within (TenantId, code).
// Required parent missing/unresolved ⇒ row Failed; optional provided-but-unresolved ⇒ Failed; omitted ⇒ null.

internal static class InboundResolve
{
    public static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Loads a (code → id) map for the supplied codes within the tenant. Soft-delete aware.</summary>
    public static async Task<Dictionary<string, Guid>> LoadMapAsync<T>(
        IQueryable<T> set, Guid tenantId, IEnumerable<string> codes, CancellationToken ct)
        where T : Domain.Common.AuditableEntity, Domain.Common.ITenantOwned, Domain.Common.IHasCode
    {
        var wanted = codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        var rows = await set.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && wanted.Contains(x.Code))
            .Select(x => new { x.Code, x.Id }).ToListAsync(ct);
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) map[r.Code] = r.Id;
        return map;
    }
}

// ----------------------------------- Currency -----------------------------------
public record UpsertCurrenciesCommand(PushCurrenciesRequest Body, string? IdempotencyKey) : IRequest<UpsertResultDto>;
public class UpsertCurrenciesCommandValidator : AbstractValidator<UpsertCurrenciesCommand>
{
    public UpsertCurrenciesCommandValidator()
    {
        RuleFor(x => x.Body.Records).NotEmpty().Must(r => r == null || r.Count <= 1000).WithMessage("A batch may contain at most 1000 records.");
        RuleForEach(x => x.Body.Records).ChildRules(r =>
        {
            r.RuleFor(c => c.Code).NotEmpty().MaximumLength(10);
            r.RuleFor(c => c.Description).MaximumLength(100);
        });
    }
}
public class UpsertCurrenciesCommandHandler(TenantInboundUpsertExecutor exec) : IRequestHandler<UpsertCurrenciesCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertCurrenciesCommand request, CancellationToken ct)
    {
        var recs = request.Body.Records;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{(r.Description ?? "").Trim()}|{r.IsoCode}|{r.Symbol}|{r.DecimalPlaces}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(TenantInboundEntity.Currency, request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.Currencies.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && !c.IsDeleted && codeList.Contains(c.Code)).ToListAsync(token))
                .ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId;
                    row.Description = (rec.Description ?? "").Trim();
                    row.IsoCode = InboundResolve.Norm(rec.IsoCode);
                    row.Symbol = InboundResolve.Norm(rec.Symbol);
                    row.DecimalPlaces = rec.DecimalPlaces;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.Currencies.Add(new Currency
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, Code = code,
                        Description = (rec.Description ?? "").Trim(), IsoCode = InboundResolve.Norm(rec.IsoCode),
                        Symbol = InboundResolve.Norm(rec.Symbol), DecimalPlaces = rec.DecimalPlaces, IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}

// ----------------------------------- Country -----------------------------------
public record UpsertCountriesCommand(PushCountriesRequest Body, string? IdempotencyKey) : IRequest<UpsertResultDto>;
public class UpsertCountriesCommandValidator : AbstractValidator<UpsertCountriesCommand>
{
    public UpsertCountriesCommandValidator()
    {
        RuleFor(x => x.Body.Records).NotEmpty().Must(r => r == null || r.Count <= 1000);
        RuleForEach(x => x.Body.Records).ChildRules(r =>
        {
            r.RuleFor(c => c.Code).NotEmpty().MaximumLength(10);
            r.RuleFor(c => c.Description).MaximumLength(150);
        });
    }
}
public class UpsertCountriesCommandHandler(TenantInboundUpsertExecutor exec) : IRequestHandler<UpsertCountriesCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertCountriesCommand request, CancellationToken ct)
    {
        var recs = request.Body.Records;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{(r.Description ?? "").Trim()}|{r.IsoCode2}|{r.IsoCode3}|{r.TelephoneCode}|{r.CurrencyCode}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(TenantInboundEntity.Country, request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.Countries.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && !c.IsDeleted && codeList.Contains(c.Code)).ToListAsync(token))
                .ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
            var currencyMap = await InboundResolve.LoadMapAsync(db.Currencies, tenantId, recs.Select(r => r.CurrencyCode!).Where(c => c != null), token);
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                Guid? currencyId = null;
                if (!string.IsNullOrWhiteSpace(rec.CurrencyCode))
                {
                    if (!currencyMap.TryGetValue(rec.CurrencyCode.Trim(), out var cid))
                    { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown currency code '{rec.CurrencyCode}'.")); continue; }
                    currencyId = cid;
                }
                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId;
                    row.Description = (rec.Description ?? "").Trim();
                    row.IsoCode2 = InboundResolve.Norm(rec.IsoCode2);
                    row.IsoCode3 = InboundResolve.Norm(rec.IsoCode3);
                    row.TelephoneCode = InboundResolve.Norm(rec.TelephoneCode);
                    row.CurrencyId = currencyId;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.Countries.Add(new Country
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, Code = code, Description = (rec.Description ?? "").Trim(),
                        IsoCode2 = InboundResolve.Norm(rec.IsoCode2), IsoCode3 = InboundResolve.Norm(rec.IsoCode3),
                        TelephoneCode = InboundResolve.Norm(rec.TelephoneCode), CurrencyId = currencyId, IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}

// ----------------------------------- State -----------------------------------
public record UpsertStatesCommand(PushStatesRequest Body, string? IdempotencyKey) : IRequest<UpsertResultDto>;
public class UpsertStatesCommandValidator : AbstractValidator<UpsertStatesCommand>
{
    public UpsertStatesCommandValidator()
    {
        RuleFor(x => x.Body.Records).NotEmpty().Must(r => r == null || r.Count <= 1000);
        RuleForEach(x => x.Body.Records).ChildRules(r =>
        {
            r.RuleFor(s => s.Code).NotEmpty().MaximumLength(20);
            r.RuleFor(s => s.Description).MaximumLength(150);
            r.RuleFor(s => s.CountryCode).NotEmpty();
        });
    }
}
public class UpsertStatesCommandHandler(TenantInboundUpsertExecutor exec) : IRequestHandler<UpsertStatesCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertStatesCommand request, CancellationToken ct)
    {
        var recs = request.Body.Records;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{(r.Description ?? "").Trim()}|{r.CountryCode}|{r.IsoCode}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(TenantInboundEntity.State, request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.States.IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && !s.IsDeleted && codeList.Contains(s.Code)).ToListAsync(token))
                .ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
            var countryMap = await InboundResolve.LoadMapAsync(db.Countries, tenantId, recs.Select(r => r.CountryCode), token);
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                if (!countryMap.TryGetValue((rec.CountryCode ?? "").Trim(), out var countryId))
                { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown country code '{rec.CountryCode}'.")); continue; }

                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId;
                    row.Description = (rec.Description ?? "").Trim();
                    row.CountryId = countryId;
                    row.IsoCode = InboundResolve.Norm(rec.IsoCode);
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.States.Add(new State
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, Code = code, Description = (rec.Description ?? "").Trim(),
                        CountryId = countryId, IsoCode = InboundResolve.Norm(rec.IsoCode), IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}

// ----------------------------------- City -----------------------------------
public record UpsertCitiesCommand(PushCitiesRequest Body, string? IdempotencyKey) : IRequest<UpsertResultDto>;
public class UpsertCitiesCommandValidator : AbstractValidator<UpsertCitiesCommand>
{
    public UpsertCitiesCommandValidator()
    {
        RuleFor(x => x.Body.Records).NotEmpty().Must(r => r == null || r.Count <= 1000);
        RuleForEach(x => x.Body.Records).ChildRules(r =>
        {
            r.RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
            r.RuleFor(c => c.Description).MaximumLength(150);
            r.RuleFor(c => c.CountryCode).NotEmpty();
        });
    }
}
public class UpsertCitiesCommandHandler(TenantInboundUpsertExecutor exec) : IRequestHandler<UpsertCitiesCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertCitiesCommand request, CancellationToken ct)
    {
        var recs = request.Body.Records;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{(r.Description ?? "").Trim()}|{r.CountryCode}|{r.StateCode}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(TenantInboundEntity.City, request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.Cities.IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId && !c.IsDeleted && codeList.Contains(c.Code)).ToListAsync(token))
                .ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
            var countryMap = await InboundResolve.LoadMapAsync(db.Countries, tenantId, recs.Select(r => r.CountryCode), token);
            var stateMap = await InboundResolve.LoadMapAsync(db.States, tenantId, recs.Select(r => r.StateCode!).Where(c => c != null), token);
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                if (!countryMap.TryGetValue((rec.CountryCode ?? "").Trim(), out var countryId))
                { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown country code '{rec.CountryCode}'.")); continue; }
                Guid? stateId = null;
                if (!string.IsNullOrWhiteSpace(rec.StateCode))
                {
                    if (!stateMap.TryGetValue(rec.StateCode.Trim(), out var sid))
                    { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown state code '{rec.StateCode}'.")); continue; }
                    stateId = sid;
                }
                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId;
                    row.Description = (rec.Description ?? "").Trim();
                    row.CountryId = countryId;
                    row.StateId = stateId;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.Cities.Add(new City
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, Code = code, Description = (rec.Description ?? "").Trim(),
                        CountryId = countryId, StateId = stateId, IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}

// ----------------------------------- PostalCode -----------------------------------
public record UpsertPostalCodesCommand(PushPostalCodesRequest Body, string? IdempotencyKey) : IRequest<UpsertResultDto>;
public class UpsertPostalCodesCommandValidator : AbstractValidator<UpsertPostalCodesCommand>
{
    public UpsertPostalCodesCommandValidator()
    {
        RuleFor(x => x.Body.Records).NotEmpty().Must(r => r == null || r.Count <= 1000);
        RuleForEach(x => x.Body.Records).ChildRules(r =>
        {
            r.RuleFor(p => p.Code).NotEmpty().MaximumLength(20);
            r.RuleFor(p => p.CountryCode).NotEmpty();
        });
    }
}
public class UpsertPostalCodesCommandHandler(TenantInboundUpsertExecutor exec) : IRequestHandler<UpsertPostalCodesCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertPostalCodesCommand request, CancellationToken ct)
    {
        var recs = request.Body.Records;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{r.Area}|{r.CountryCode}|{r.StateCode}|{r.CityCode}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(TenantInboundEntity.PostalCode, request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.PostalCodes.IgnoreQueryFilters()
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && codeList.Contains(p.Code)).ToListAsync(token))
                .ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
            var countryMap = await InboundResolve.LoadMapAsync(db.Countries, tenantId, recs.Select(r => r.CountryCode), token);
            var stateMap = await InboundResolve.LoadMapAsync(db.States, tenantId, recs.Select(r => r.StateCode!).Where(c => c != null), token);
            var cityMap = await InboundResolve.LoadMapAsync(db.Cities, tenantId, recs.Select(r => r.CityCode!).Where(c => c != null), token);
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                if (!countryMap.TryGetValue((rec.CountryCode ?? "").Trim(), out var countryId))
                { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown country code '{rec.CountryCode}'.")); continue; }
                Guid? stateId = null;
                if (!string.IsNullOrWhiteSpace(rec.StateCode))
                {
                    if (!stateMap.TryGetValue(rec.StateCode.Trim(), out var sid))
                    { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown state code '{rec.StateCode}'.")); continue; }
                    stateId = sid;
                }
                Guid? cityId = null;
                if (!string.IsNullOrWhiteSpace(rec.CityCode))
                {
                    if (!cityMap.TryGetValue(rec.CityCode.Trim(), out var cid))
                    { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown city code '{rec.CityCode}'.")); continue; }
                    cityId = cid;
                }
                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId;
                    row.Area = InboundResolve.Norm(rec.Area);
                    row.CountryId = countryId;
                    row.StateId = stateId;
                    row.CityId = cityId;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.PostalCodes.Add(new PostalCode
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, Code = code, Area = InboundResolve.Norm(rec.Area),
                        CountryId = countryId, StateId = stateId, CityId = cityId, IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}
