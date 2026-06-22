using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// R4 (2026-06-22) — Module 5 / Increment D (H1: inbound Payment sync). Writes <c>proc.Payment</c> rows pushed
/// by Infor LN (the only writer of payments — payments originate in the ERP). The invoice is resolved by
/// <c>InvoiceErpSyncId</c> (preferred, deterministic) else <c>InvoiceNumber</c>; the row links via
/// <c>InvoiceId</c>, correlates on <c>ErpSyncId</c>, carries <c>PaymentReference</c> and the received amount via
/// <c>NetPaid</c>. Upsert key = (invoiceId, paymentReference) so a re-push of the same remittance is an update,
/// not a duplicate. Reuses <see cref="InboundUpsertExecutor"/> (company resolution, anti-spoof, endpoint gate,
/// canonical-hash idempotency, transactional SyncLog/IntegrationError, endpoint-session telemetry).
/// Payment rows inherit the resolved invoice's seccode (so the supplier sees its own remittances under RLS).
/// </summary>
public record UpsertPaymentsCommand(
    PushPaymentsRequest Body,
    IReadOnlySet<Guid> BoundCompanyIds,
    string? IdempotencyKey) : IRequest<UpsertResultDto>;

public class UpsertPaymentsCommandValidator : AbstractValidator<UpsertPaymentsCommand>
{
    public UpsertPaymentsCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Payments).NotEmpty().Must(p => p == null || p.Count <= 1000)
            .WithMessage("Between 1 and 1000 payments per batch.");
        RuleForEach(x => x.Body.Payments).ChildRules(p =>
        {
            p.RuleFor(r => r.PaymentReference).NotEmpty().MaximumLength(100);
            p.RuleFor(r => r.NetPaid).GreaterThanOrEqualTo(0);
            p.RuleFor(r => r)
                .Must(r => !string.IsNullOrWhiteSpace(r.InvoiceErpSyncId) || !string.IsNullOrWhiteSpace(r.InvoiceNumber))
                .WithMessage("Each payment must carry invoiceErpSyncId or invoiceNumber to resolve the invoice.");
        });
    }
}

public class UpsertPaymentsCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertPaymentsCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertPaymentsCommand request, CancellationToken ct)
    {
        var recs = request.Body.Payments;
        var canonical = recs.Select(r =>
            $"{r.PaymentReference.Trim().ToUpperInvariant()}|{r.NetPaid}|{(r.InvoiceErpSyncId ?? "").Trim()}|{(r.InvoiceNumber ?? "").Trim()}|{(r.ErpSyncId ?? "").Trim()}");
        var codes = recs.Select(r => r.PaymentReference.Trim());

        return exec.ExecuteAsync(TransactionalInboundEntity.Payment, request.Body.CompanyCode, request.BoundCompanyIds,
            request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);

            // Resolve the target invoices by erpSyncId / invoiceNumber within the resolved company.
            var erpSyncIds = recs.Where(r => !string.IsNullOrWhiteSpace(r.InvoiceErpSyncId))
                .Select(r => r.InvoiceErpSyncId!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var invoiceNumbers = recs.Where(r => !string.IsNullOrWhiteSpace(r.InvoiceNumber))
                .Select(r => r.InvoiceNumber!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var invoices = await db.Invoices.IgnoreQueryFilters()
                .Where(i => !i.IsDeleted && i.TenantId == tenantId && i.TenantEntityId == sourceId
                            && ((i.ErpSyncId != null && erpSyncIds.Contains(i.ErpSyncId)) || invoiceNumbers.Contains(i.InvoiceNumber)))
                .Select(i => new { i.Id, i.InvoiceNumber, i.ErpSyncId, i.SupplierId, i.SeccodeId })
                .ToListAsync(token);
            var byErpSyncId = invoices.Where(i => i.ErpSyncId != null)
                .GroupBy(i => i.ErpSyncId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var byNumber = invoices
                .GroupBy(i => i.InvoiceNumber, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Existing payments to upsert on (invoiceId, paymentReference).
            var refs = recs.Select(r => r.PaymentReference.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = (await db.Payments.IgnoreQueryFilters()
                    .Where(p => !p.IsDeleted && p.TenantId == tenantId && p.TenantEntityId == sourceId && refs.Contains(p.PaymentReference))
                    .ToListAsync(token))
                .ToDictionary(p => $"{p.InvoiceId:N}|{p.PaymentReference}", StringComparer.OrdinalIgnoreCase);

            foreach (var rec in recs)
            {
                var payRef = rec.PaymentReference.Trim();

                var inv = (!string.IsNullOrWhiteSpace(rec.InvoiceErpSyncId) && byErpSyncId.TryGetValue(rec.InvoiceErpSyncId.Trim(), out var bySync))
                    ? bySync
                    : (!string.IsNullOrWhiteSpace(rec.InvoiceNumber) && byNumber.TryGetValue(rec.InvoiceNumber.Trim(), out var byNum)) ? byNum : null;

                if (inv is null)
                {
                    results.Add(new RowResult(payRef, RowOutcome.Failed,
                        $"No invoice for erpSyncId '{rec.InvoiceErpSyncId}' / number '{rec.InvoiceNumber}' in the resolved company."));
                    continue;
                }

                var dupKey = $"{inv.Id:N}|{payRef}";
                if (existing.TryGetValue(dupKey, out var row))
                {
                    row.NetPaid = rec.NetPaid;
                    row.PaymentAmount = rec.PaymentAmount ?? rec.NetPaid;
                    row.TdsDeducted = rec.TdsDeducted ?? row.TdsDeducted;
                    if (rec.PaymentDate.HasValue) row.PaymentDate = rec.PaymentDate.Value;
                    if (!string.IsNullOrWhiteSpace(rec.PaymentMode)) row.PaymentMode = rec.PaymentMode!.Trim();
                    if (!string.IsNullOrWhiteSpace(rec.ErpSyncId)) row.ErpSyncId = rec.ErpSyncId!.Trim();
                    if (!string.IsNullOrWhiteSpace(rec.ErpCode)) row.ErpCode = rec.ErpCode!.Trim();
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(payRef, RowOutcome.Updated, null));
                }
                else
                {
                    var pay = new Payment
                    {
                        Id = Guid.NewGuid(),
                        PaymentReference = payRef,
                        InvoiceId = inv.Id,
                        SupplierId = inv.SupplierId,
                        PaymentDate = rec.PaymentDate ?? now,
                        PaymentAmount = rec.PaymentAmount ?? rec.NetPaid,
                        TdsDeducted = rec.TdsDeducted ?? 0m,
                        NetPaid = rec.NetPaid,
                        PaymentMode = string.IsNullOrWhiteSpace(rec.PaymentMode) ? null : rec.PaymentMode!.Trim(),
                        ErpSyncId = string.IsNullOrWhiteSpace(rec.ErpSyncId) ? null : rec.ErpSyncId!.Trim(),
                        ErpCode = string.IsNullOrWhiteSpace(rec.ErpCode) ? null : rec.ErpCode!.Trim(),
                        // Inherit the invoice's seccode + scope so the supplier sees its own remittance under RLS.
                        SeccodeId = inv.SeccodeId,
                        TenantId = tenantId,
                        TenantEntityId = sourceId,
                        CreatedBy = "infor:inbound",
                        CreatedOn = now,
                    };
                    db.Payments.Add(pay);
                    existing[dupKey] = pay;   // allow a later row in the batch with the same key to update it.
                    results.Add(new RowResult(payRef, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}
