using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using UnitEntity = MerinoOne.SupplierPortal.Domain.Entities.Mdm.Unit;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

// Inbound company-scoped masters (Unit / ItemGroup / Item). CompanyCode is resolved + normalized to the
// share-group source by InboundUpsertExecutor; parents are resolved by (sourceId, code).

// ----------------------------------- Unit -----------------------------------
public record UpsertUnitsCommand(PushUnitsRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey) : IRequest<UpsertResultDto>;
public class UpsertUnitsCommandValidator : AbstractValidator<UpsertUnitsCommand>
{
    public UpsertUnitsCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Units).NotEmpty().Must(u => u == null || u.Count <= 1000);
        RuleForEach(x => x.Body.Units).ChildRules(u =>
        {
            u.RuleFor(r => r.Code).NotEmpty().MaximumLength(20);
            u.RuleFor(r => r.Description).MaximumLength(150);
        });
    }
}
public class UpsertUnitsCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertUnitsCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertUnitsCommand request, CancellationToken ct)
    {
        var recs = request.Body.Units;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{(r.Description ?? "").Trim()}|{r.UnitType}|{r.IsoCode}|{r.DecimalPlaces}|{r.ConversionFactor}|{r.BaseUnitCode}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(SharedEndpoint.Unit, request.Body.CompanyCode, request.BoundCompanyIds, request.IdempotencyKey,
            recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.Units.IgnoreQueryFilters()
                .Where(u => u.TenantEntityId == sourceId && !u.IsDeleted && codeList.Contains(u.Code)).ToListAsync(token))
                .ToDictionary(u => u.Code, StringComparer.OrdinalIgnoreCase);
            // Base-unit codes may reference rows already in this batch or pre-existing under the source.
            var baseCodes = recs.Where(r => !string.IsNullOrWhiteSpace(r.BaseUnitCode)).Select(r => r.BaseUnitCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var baseMap = baseCodes.Count == 0 ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                : (await db.Units.IgnoreQueryFilters().Where(u => u.TenantEntityId == sourceId && !u.IsDeleted && baseCodes.Contains(u.Code))
                    .Select(u => new { u.Code, u.Id }).ToListAsync(token)).ToDictionary(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                Guid? baseUnitId = null;
                if (!string.IsNullOrWhiteSpace(rec.BaseUnitCode))
                {
                    if (existing.TryGetValue(rec.BaseUnitCode.Trim(), out var inBatch)) baseUnitId = inBatch.Id;
                    else if (baseMap.TryGetValue(rec.BaseUnitCode.Trim(), out var bid)) baseUnitId = bid;
                    else { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown base unit code '{rec.BaseUnitCode}'.")); continue; }
                }
                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId; row.TenantEntityId = sourceId;
                    row.Description = (rec.Description ?? "").Trim();
                    row.UnitType = Enum.TryParse<UnitType>(rec.UnitType, true, out var t) ? t : UnitType.Quantity;
                    row.IsoCode = InboundResolve.Norm(rec.IsoCode);
                    row.DecimalPlaces = rec.DecimalPlaces;
                    row.ConversionFactor = rec.ConversionFactor;
                    row.BaseUnitId = baseUnitId;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound"; row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    var e = new UnitEntity
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = sourceId, Code = code,
                        Description = (rec.Description ?? "").Trim(),
                        UnitType = Enum.TryParse<UnitType>(rec.UnitType, true, out var t) ? t : UnitType.Quantity,
                        IsoCode = InboundResolve.Norm(rec.IsoCode), DecimalPlaces = rec.DecimalPlaces,
                        ConversionFactor = rec.ConversionFactor, BaseUnitId = baseUnitId, IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    };
                    db.Units.Add(e);
                    existing[code] = e;   // allow later rows in the batch to reference it as a base unit
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}

// ----------------------------------- ItemGroup -----------------------------------
public record UpsertItemGroupsCommand(PushItemGroupsRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey) : IRequest<UpsertResultDto>;
public class UpsertItemGroupsCommandValidator : AbstractValidator<UpsertItemGroupsCommand>
{
    public UpsertItemGroupsCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.ItemGroups).NotEmpty().Must(g => g == null || g.Count <= 1000);
        RuleForEach(x => x.Body.ItemGroups).ChildRules(g =>
        {
            g.RuleFor(r => r.Code).NotEmpty().MaximumLength(20);
            g.RuleFor(r => r.Description).MaximumLength(200);
        });
    }
}
public class UpsertItemGroupsCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertItemGroupsCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertItemGroupsCommand request, CancellationToken ct)
    {
        var recs = request.Body.ItemGroups;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{(r.Description ?? "").Trim()}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(SharedEndpoint.ItemGroup, request.Body.CompanyCode, request.BoundCompanyIds, request.IdempotencyKey,
            recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.ItemGroups.IgnoreQueryFilters()
                .Where(g => g.TenantEntityId == sourceId && !g.IsDeleted && codeList.Contains(g.Code)).ToListAsync(token))
                .ToDictionary(g => g.Code, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId; row.TenantEntityId = sourceId;
                    row.Description = (rec.Description ?? "").Trim();
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound"; row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.ItemGroups.Add(new ItemGroup
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = sourceId, Code = code,
                        Description = (rec.Description ?? "").Trim(), IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}

// ----------------------------------- Item -----------------------------------
public record UpsertItemsCommand(PushItemsRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey) : IRequest<UpsertResultDto>;
public class UpsertItemsCommandValidator : AbstractValidator<UpsertItemsCommand>
{
    public UpsertItemsCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Items).NotEmpty().Must(i => i == null || i.Count <= 1000);
        RuleForEach(x => x.Body.Items).ChildRules(i =>
        {
            i.RuleFor(r => r.Code).NotEmpty().MaximumLength(50);
            i.RuleFor(r => r.Description).MaximumLength(500);
        });
    }
}
public class UpsertItemsCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertItemsCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertItemsCommand request, CancellationToken ct)
    {
        var recs = request.Body.Items;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{(r.Description ?? "").Trim()}|{r.UnitCode}|{r.ItemGroupCode}|{r.HsnCode}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(SharedEndpoint.Item, request.Body.CompanyCode, request.BoundCompanyIds, request.IdempotencyKey,
            recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.Items.IgnoreQueryFilters()
                .Where(i => i.TenantEntityId == sourceId && !i.IsDeleted && codeList.Contains(i.Code)).ToListAsync(token))
                .ToDictionary(i => i.Code, StringComparer.OrdinalIgnoreCase);

            var unitCodes = recs.Where(r => !string.IsNullOrWhiteSpace(r.UnitCode)).Select(r => r.UnitCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var unitMap = unitCodes.Count == 0 ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                : (await db.Units.IgnoreQueryFilters().Where(u => u.TenantEntityId == sourceId && !u.IsDeleted && unitCodes.Contains(u.Code))
                    .Select(u => new { u.Code, u.Id }).ToListAsync(token)).ToDictionary(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase);
            var groupCodes = recs.Where(r => !string.IsNullOrWhiteSpace(r.ItemGroupCode)).Select(r => r.ItemGroupCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var groupMap = groupCodes.Count == 0 ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                : (await db.ItemGroups.IgnoreQueryFilters().Where(g => g.TenantEntityId == sourceId && !g.IsDeleted && groupCodes.Contains(g.Code))
                    .Select(g => new { g.Code, g.Id }).ToListAsync(token)).ToDictionary(g => g.Code, g => g.Id, StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                Guid? unitId = null, groupId = null;
                if (!string.IsNullOrWhiteSpace(rec.UnitCode))
                {
                    if (!unitMap.TryGetValue(rec.UnitCode.Trim(), out var uid))
                    { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown unit code '{rec.UnitCode}'.")); continue; }
                    unitId = uid;
                }
                if (!string.IsNullOrWhiteSpace(rec.ItemGroupCode))
                {
                    if (!groupMap.TryGetValue(rec.ItemGroupCode.Trim(), out var gid))
                    { results.Add(new RowResult(code, RowOutcome.Failed, $"Unknown item group code '{rec.ItemGroupCode}'.")); continue; }
                    groupId = gid;
                }
                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId; row.TenantEntityId = sourceId;
                    row.Description = (rec.Description ?? "").Trim();
                    row.HsnCode = InboundResolve.Norm(rec.HsnCode);
                    row.UnitId = unitId; row.ItemGroupId = groupId;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound"; row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.Items.Add(new Item
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = sourceId, Code = code,
                        Description = (rec.Description ?? "").Trim(),
                        HsnCode = InboundResolve.Norm(rec.HsnCode), UnitId = unitId, ItemGroupId = groupId, IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}
