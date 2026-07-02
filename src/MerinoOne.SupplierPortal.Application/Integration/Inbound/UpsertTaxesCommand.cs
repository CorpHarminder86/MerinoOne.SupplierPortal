using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

// R4 (2026-06-23) — Module 6: inbound Tax master (company-shared / ICompanyScoped, ERP-fed per Q6).
// Mirrors the ItemGroup inbound upsert (share-aware company resolution via SharedEndpoint.Tax — CompanyCode is
// normalized to the share-group source by InboundUpsertExecutor; upsert keyed on (sourceId, Code)).
public record UpsertTaxesCommand(PushTaxesRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey) : IRequest<UpsertResultDto>;

public class UpsertTaxesCommandValidator : AbstractValidator<UpsertTaxesCommand>
{
    public UpsertTaxesCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Taxes).NotEmpty().Must(t => t == null || t.Count <= 1000);
        RuleForEach(x => x.Body.Taxes).ChildRules(t =>
        {
            t.RuleFor(r => r.Code).NotEmpty().MaximumLength(20);
            t.RuleFor(r => r.Description).MaximumLength(200);
        });
    }
}

public class UpsertTaxesCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertTaxesCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertTaxesCommand request, CancellationToken ct)
    {
        var recs = request.Body.Taxes;
        var canonical = recs.Select(r => $"{r.Code.Trim().ToUpperInvariant()}|{(r.Description ?? "").Trim()}|{r.TaxRate}|{r.IsActive}");
        var codes = recs.Select(r => r.Code.Trim());
        return exec.ExecuteAsync(SharedEndpoint.Tax, request.Body.CompanyCode, request.BoundCompanyIds, request.IdempotencyKey,
            recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var codeList = recs.Select(r => r.Code.Trim()).ToList();
            var existing = (await db.Taxes.IgnoreQueryFilters()
                    .Where(t => t.TenantEntityId == sourceId && !t.IsDeleted && codeList.Contains(t.Code)).ToListAsync(token))
                .ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);
            foreach (var rec in recs)
            {
                var code = rec.Code.Trim();
                if (existing.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId; row.TenantEntityId = sourceId;
                    row.Description = (rec.Description ?? "").Trim();
                    // R6 — an admin-pinned rate (IsRateOverridden) WINS over the sync: TaxRate is written only
                    // when not overridden. LastSyncedRate ALWAYS tracks the latest inbound value regardless, so
                    // the drift stays visible and the admin can reset the override back to the synced rate.
                    if (!row.IsRateOverridden) row.TaxRate = rec.TaxRate;
                    row.LastSyncedRate = rec.TaxRate;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound"; row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.Taxes.Add(new Tax
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = sourceId, Code = code,
                        Description = (rec.Description ?? "").Trim(), TaxRate = rec.TaxRate, IsActive = rec.IsActive,
                        LastSyncedRate = rec.TaxRate,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}
