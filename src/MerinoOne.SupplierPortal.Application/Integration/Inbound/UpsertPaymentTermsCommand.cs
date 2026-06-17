using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// Inbound Payment Term upsert (consumed by Infor LN via X-APIKey). See <see cref="InboundUpsertExecutor"/>
/// for the full algorithm (company resolution, share-group normalization, anti-spoof, endpoint gate,
/// idempotency, transactional per-row upsert, SyncLog/IntegrationError + endpoint session update).
/// <paramref name="BoundCompanyIds"/> is the key's bound source-company set (one per tenantEntityId claim,
/// Feature C — multi-company keys) — the controller reads it from the API-key principal and passes it in
/// for the anti-spoof check.
/// </summary>
public record UpsertPaymentTermsCommand(PushPaymentTermsRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey)
    : IRequest<UpsertResultDto>;

public class UpsertPaymentTermsCommandValidator : AbstractValidator<UpsertPaymentTermsCommand>
{
    public UpsertPaymentTermsCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Terms)
            .NotEmpty().WithMessage("At least one term is required.")
            .Must(t => t == null || t.Count <= 1000).WithMessage("A batch may contain at most 1000 terms.");

        RuleForEach(x => x.Body.Terms).ChildRules(t =>
        {
            t.RuleFor(r => r.Code).NotEmpty().MaximumLength(20);
            t.RuleFor(r => r.Description).MaximumLength(200);
            t.RuleFor(r => r.NetDays).InclusiveBetween(0, 365);
        });

        // No duplicate Code within the batch (case-insensitive) — would make the per-(source,Code) upsert ambiguous.
        RuleFor(x => x.Body.Terms)
            .Must(NoDuplicateCodes)
            .When(x => x.Body.Terms is { Count: > 0 })
            .WithMessage("Duplicate term Code values are not allowed in a single batch.");
    }

    private static bool NoDuplicateCodes(IReadOnlyList<PaymentTermRecord> terms)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in terms)
            if (!string.IsNullOrWhiteSpace(t.Code) && !seen.Add(t.Code.Trim()))
                return false;
        return true;
    }
}

public class UpsertPaymentTermsCommandHandler : IRequestHandler<UpsertPaymentTermsCommand, UpsertResultDto>
{
    private readonly InboundUpsertExecutor _executor;
    public UpsertPaymentTermsCommandHandler(InboundUpsertExecutor executor) => _executor = executor;

    public async Task<UpsertResultDto> Handle(UpsertPaymentTermsCommand request, CancellationToken ct)
    {
        var terms = request.Body.Terms;

        // Canonical rows fold every field that distinguishes a record so a true replay short-circuits
        // but an edited body does not.
        var canonicalRows = terms.Select(t =>
            $"{t.Code.Trim().ToUpperInvariant()}|{(t.Description ?? string.Empty).Trim()}|{t.NetDays}|{t.IsActive}");
        var codes = terms.Select(t => t.Code.Trim());

        return await _executor.ExecuteAsync(
            SharedEndpoint.PaymentTerm,
            request.Body.CompanyCode,
            request.BoundCompanyIds,
            request.IdempotencyKey,
            terms.Count,
            canonicalRows,
            codes,
            request.Body,
            UpsertRowsAsync,
            ct);

        async Task<IReadOnlyList<RowResult>> UpsertRowsAsync(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var codes = terms.Select(t => t.Code.Trim()).ToList();

            // Pre-load existing rows for the whole batch in ONE query, keyed on (sourceId, Code).
            // IgnoreQueryFilters: the service principal has no readable company context; key + tenant scope
            // are applied explicitly. Re-apply !IsDeleted.
            var existing = await db.PaymentTerms.IgnoreQueryFilters()
                .Where(p => p.TenantEntityId == sourceId && !p.IsDeleted && codes.Contains(p.Code))
                .ToListAsync(token);
            var byCode = existing.ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);

            var results = new List<RowResult>(terms.Count);
            foreach (var rec in terms)
            {
                var code = rec.Code.Trim();
                if (byCode.TryGetValue(code, out var row))
                {
                    row.TenantId = tenantId;
                    row.Description = (rec.Description ?? string.Empty).Trim();
                    row.NetDays = rec.NetDays;
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.PaymentTerms.Add(new PaymentTerm
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        TenantEntityId = sourceId,
                        Code = code,
                        Description = (rec.Description ?? string.Empty).Trim(),
                        NetDays = rec.NetDays,
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
}
