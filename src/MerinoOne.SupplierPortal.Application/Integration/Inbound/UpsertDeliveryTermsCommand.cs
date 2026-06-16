using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// Inbound Delivery Term upsert (consumed by Infor LN via X-APIKey). Mirrors
/// <see cref="UpsertPaymentTermsCommand"/>; shares <see cref="InboundUpsertExecutor"/> for the
/// cross-cutting algorithm. <paramref name="BoundCompanyId"/> is the key's bound source company.
/// </summary>
public record UpsertDeliveryTermsCommand(PushDeliveryTermsRequest Body, Guid? BoundCompanyId, string? IdempotencyKey)
    : IRequest<UpsertResultDto>;

public class UpsertDeliveryTermsCommandValidator : AbstractValidator<UpsertDeliveryTermsCommand>
{
    public UpsertDeliveryTermsCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Terms)
            .NotEmpty().WithMessage("At least one term is required.")
            .Must(t => t == null || t.Count <= 1000).WithMessage("A batch may contain at most 1000 terms.");

        RuleForEach(x => x.Body.Terms).ChildRules(t =>
        {
            t.RuleFor(r => r.Code).NotEmpty().MaximumLength(20);
            t.RuleFor(r => r.Description).MaximumLength(200);
        });

        RuleFor(x => x.Body.Terms)
            .Must(NoDuplicateCodes)
            .When(x => x.Body.Terms is { Count: > 0 })
            .WithMessage("Duplicate term Code values are not allowed in a single batch.");
    }

    private static bool NoDuplicateCodes(IReadOnlyList<DeliveryTermRecord> terms)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in terms)
            if (!string.IsNullOrWhiteSpace(t.Code) && !seen.Add(t.Code.Trim()))
                return false;
        return true;
    }
}

public class UpsertDeliveryTermsCommandHandler : IRequestHandler<UpsertDeliveryTermsCommand, UpsertResultDto>
{
    private readonly InboundUpsertExecutor _executor;
    public UpsertDeliveryTermsCommandHandler(InboundUpsertExecutor executor) => _executor = executor;

    public async Task<UpsertResultDto> Handle(UpsertDeliveryTermsCommand request, CancellationToken ct)
    {
        var terms = request.Body.Terms;

        var canonicalRows = terms.Select(t =>
            $"{t.Code.Trim().ToUpperInvariant()}|{(t.Description ?? string.Empty).Trim()}|{t.IsActive}");

        return await _executor.ExecuteAsync(
            SharedEndpoint.DeliveryTerm,
            request.Body.CompanyCode,
            request.BoundCompanyId,
            request.IdempotencyKey,
            terms.Count,
            canonicalRows,
            UpsertRowsAsync,
            ct);

        async Task<IReadOnlyList<RowResult>> UpsertRowsAsync(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var codes = terms.Select(t => t.Code.Trim()).ToList();

            var existing = await db.DeliveryTerms.IgnoreQueryFilters()
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
                    row.IsActive = rec.IsActive;
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Updated, null));
                }
                else
                {
                    db.DeliveryTerms.Add(new DeliveryTerm
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        TenantEntityId = sourceId,
                        Code = code,
                        Description = (rec.Description ?? string.Empty).Trim(),
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
