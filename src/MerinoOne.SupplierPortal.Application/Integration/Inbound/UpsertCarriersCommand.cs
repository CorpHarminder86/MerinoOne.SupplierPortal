using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// R17 (2026-10-26) — inbound Carrier master upsert (consumed by Infor LN via X-APIKey). A clone of
/// <see cref="UpsertDeliveryTermsCommand"/>: company-shared (share-group source company), keyed on
/// (source company, Code) case-insensitively, shares <see cref="InboundUpsertExecutor"/> for the cross-cutting
/// algorithm. The two deliberate differences from the delivery-term master: Code is capped at 9 (the
/// <c>proc.carrier.code</c> column) and Description is OPTIONAL (at most 50) — LN can push a carrier code with no name.
/// <paramref name="BoundCompanyIds"/> is the key's bound source-company set (Feature C — multi-company keys).
/// </summary>
public record UpsertCarriersCommand(PushCarriersRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey)
    : IRequest<UpsertResultDto>;

public class UpsertCarriersCommandValidator : AbstractValidator<UpsertCarriersCommand>
{
    public UpsertCarriersCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Carriers)
            .NotEmpty().WithMessage("At least one carrier is required.")
            .Must(c => c == null || c.Count <= 1000).WithMessage("A batch may contain at most 1000 carriers.");

        RuleForEach(x => x.Body.Carriers).ChildRules(c =>
        {
            c.RuleFor(r => r.Code).NotEmpty().MaximumLength(9);
            c.RuleFor(r => r.Description).MaximumLength(50);
        });

        RuleFor(x => x.Body.Carriers)
            .Must(NoDuplicateCodes)
            .When(x => x.Body.Carriers is { Count: > 0 })
            .WithMessage("Duplicate carrier Code values are not allowed in a single batch.");
    }

    private static bool NoDuplicateCodes(IReadOnlyList<CarrierRecord> carriers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in carriers)
            if (!string.IsNullOrWhiteSpace(c.Code) && !seen.Add(c.Code.Trim()))
                return false;
        return true;
    }
}

public class UpsertCarriersCommandHandler : IRequestHandler<UpsertCarriersCommand, UpsertResultDto>
{
    private readonly InboundUpsertExecutor _executor;
    public UpsertCarriersCommandHandler(InboundUpsertExecutor executor) => _executor = executor;

    public async Task<UpsertResultDto> Handle(UpsertCarriersCommand request, CancellationToken ct)
    {
        var carriers = request.Body.Carriers;

        var canonicalRows = carriers.Select(c =>
            $"{c.Code.Trim().ToUpperInvariant()}|{NormalizeDescription(c.Description) ?? string.Empty}|{c.IsActive}");
        var codes = carriers.Select(c => c.Code.Trim());

        return await _executor.ExecuteAsync(
            SharedEndpoint.Carrier,
            request.Body.CompanyCode,
            request.BoundCompanyIds,
            request.IdempotencyKey,
            carriers.Count,
            canonicalRows,
            codes,
            request.Body,
            UpsertRowsAsync,
            ct);

        async Task<IReadOnlyList<RowResult>> UpsertRowsAsync(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var codes = carriers.Select(c => c.Code.Trim()).ToList();

            var existing = await db.Carriers.IgnoreQueryFilters()
                .Where(c => c.TenantEntityId == sourceId && !c.IsDeleted && codes.Contains(c.Code))
                .ToListAsync(token);
            var byCode = existing.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

            var results = new List<RowResult>(carriers.Count);
            foreach (var rec in carriers)
            {
                var code = rec.Code.Trim();
                var description = NormalizeDescription(rec.Description);
                if (byCode.TryGetValue(code, out var row))
                {
                    // Only Description/IsActive are ERP-fed on an existing row (Code is the key and is never rewritten).
                    // An unchanged row is Skipped rather than touched, so a re-push does not churn UpdatedOn/the audit trail.
                    if (row.Description == description && row.IsActive == rec.IsActive)
                    {
                        results.Add(new RowResult(code, RowOutcome.Skipped, null));
                        continue;
                    }

                    row.TenantId = tenantId;
                    row.Description = description;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.Carriers.Add(new Carrier
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        TenantEntityId = sourceId,
                        Code = code,
                        Description = description,
                        IsActive = rec.IsActive,
                        CreatedBy = "infor:inbound",
                        CreatedOn = now
                    });
                    results.Add(new RowResult(code, RowOutcome.Inserted, null));
                }
            }

            return results;
        }
    }

    /// <summary>Description is optional: blank/whitespace is stored as null, anything else trimmed.</summary>
    private static string? NormalizeDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
