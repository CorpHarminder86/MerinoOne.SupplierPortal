using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Invoices;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// R4 (2026-06-22) — Module 5 / Increment D (H1: inbound invoice-status sync). Advances
/// <c>Invoice.InvoiceStatus</c> to the ERP-driven states (Matched / PartiallyPaid / Paid / Approved /
/// MatchExceptions / Rejected) pushed by Infor LN, resolving the invoice by <c>InvoiceErpSyncId</c> (preferred)
/// else <c>InvoiceNumber</c>. The writer NEVER moves an invoice backwards into Draft/Submitted (those are
/// portal-owned lifecycle states) and NEVER re-advances a Rejected invoice (terminal — its reservation was
/// already released) — an attempt is a failed row. Moving a reservation-holding invoice TO Rejected releases the
/// per-PO-line invoiced-qty reservation (plan D8) inside the executor's transaction. Reuses <see cref="InboundUpsertExecutor"/>
/// (company resolution, anti-spoof, endpoint gate, canonical-hash idempotency, transactional
/// SyncLog/IntegrationError, endpoint-session telemetry).
/// </summary>
public record UpsertInvoiceStatusCommand(
    PushInvoiceStatusRequest Body,
    IReadOnlySet<Guid> BoundCompanyIds,
    string? IdempotencyKey) : IRequest<UpsertResultDto>;

public class UpsertInvoiceStatusCommandValidator : AbstractValidator<UpsertInvoiceStatusCommand>
{
    // ERP-owned advance states. Portal-owned lifecycle states (Draft/Submitted/UnderReview/Cancelled) are NOT
    // settable via inbound — the ERP only advances an invoice forward through matching/payment.
    public static readonly IReadOnlySet<InvoiceStatus> ErpAdvanceStates = new HashSet<InvoiceStatus>
    {
        InvoiceStatus.Matched,
        InvoiceStatus.MatchExceptions,
        InvoiceStatus.Approved,
        InvoiceStatus.Rejected,
        InvoiceStatus.PartiallyPaid,
        InvoiceStatus.Paid,
    };

    public UpsertInvoiceStatusCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Invoices).NotEmpty().Must(i => i == null || i.Count <= 1000)
            .WithMessage("Between 1 and 1000 invoice-status records per batch.");
        RuleForEach(x => x.Body.Invoices).ChildRules(i =>
        {
            i.RuleFor(r => r.InvoiceStatus).NotEmpty()
                .Must(s => Enum.TryParse<InvoiceStatus>(s, true, out var st) && ErpAdvanceStates.Contains(st))
                .WithMessage(_ => $"Unknown / non-advanceable invoiceStatus. Allowed: {string.Join(", ", ErpAdvanceStates)}.");
            i.RuleFor(r => r)
                .Must(r => !string.IsNullOrWhiteSpace(r.InvoiceErpSyncId) || !string.IsNullOrWhiteSpace(r.InvoiceNumber))
                .WithMessage("Each record must carry invoiceErpSyncId or invoiceNumber to resolve the invoice.");
        });
    }
}

public class UpsertInvoiceStatusCommandHandler(
    InboundUpsertExecutor exec,
    ILogger<UpsertInvoiceStatusCommandHandler> logger) : IRequestHandler<UpsertInvoiceStatusCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertInvoiceStatusCommand request, CancellationToken ct)
    {
        var recs = request.Body.Invoices;
        var canonical = recs.Select(r =>
            $"{(r.InvoiceErpSyncId ?? "").Trim()}|{(r.InvoiceNumber ?? "").Trim()}|{r.InvoiceStatus.Trim()}|{(r.ErpCode ?? "").Trim()}");
        var codes = recs.Select(r => (r.InvoiceNumber ?? r.InvoiceErpSyncId ?? "").Trim());

        return exec.ExecuteAsync(TransactionalInboundEntity.InvoiceStatus, request.Body.CompanyCode, request.BoundCompanyIds,
            request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);

            var erpSyncIds = recs.Where(r => !string.IsNullOrWhiteSpace(r.InvoiceErpSyncId))
                .Select(r => r.InvoiceErpSyncId!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var invoiceNumbers = recs.Where(r => !string.IsNullOrWhiteSpace(r.InvoiceNumber))
                .Select(r => r.InvoiceNumber!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var invoices = await db.Invoices.IgnoreQueryFilters()
                .Where(i => !i.IsDeleted && i.TenantId == tenantId && i.TenantEntityId == sourceId
                            && ((i.ErpSyncId != null && erpSyncIds.Contains(i.ErpSyncId)) || invoiceNumbers.Contains(i.InvoiceNumber)))
                .ToListAsync(token);
            var byErpSyncId = invoices.Where(i => i.ErpSyncId != null)
                .GroupBy(i => i.ErpSyncId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var byNumber = invoices
                .GroupBy(i => i.InvoiceNumber, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var rec in recs)
            {
                var code = (rec.InvoiceNumber ?? rec.InvoiceErpSyncId ?? "").Trim();
                var newStatus = Enum.Parse<InvoiceStatus>(rec.InvoiceStatus.Trim(), ignoreCase: true);

                var inv = (!string.IsNullOrWhiteSpace(rec.InvoiceErpSyncId) && byErpSyncId.TryGetValue(rec.InvoiceErpSyncId.Trim(), out var bySync))
                    ? bySync
                    : (!string.IsNullOrWhiteSpace(rec.InvoiceNumber) && byNumber.TryGetValue(rec.InvoiceNumber.Trim(), out var byNum)) ? byNum : null;

                if (inv is null)
                {
                    results.Add(new RowResult(code, RowOutcome.Failed,
                        $"No invoice for erpSyncId '{rec.InvoiceErpSyncId}' / number '{rec.InvoiceNumber}' in the resolved company."));
                    continue;
                }

                // Never regress a portal-owned lifecycle state — the ERP only advances forward through
                // matching/payment. An invoice still in Draft/Submitted that the ERP claims is Matched/Paid is a
                // correlation error (we expect it to be posted first via the GRN cascade). Rejected is TERMINAL
                // for this writer: a portal/inbound rejection already RELEASED the invoice's per-PO-line
                // reservation (plan D8), so letting LN re-advance it would leave a paid invoice holding no
                // reservation (and a second rejection would double-release).
                if (inv.InvoiceStatus is InvoiceStatus.Draft or InvoiceStatus.Submitted or InvoiceStatus.Cancelled
                    or InvoiceStatus.Rejected)
                {
                    results.Add(new RowResult(code, RowOutcome.Failed,
                        $"Invoice '{inv.InvoiceNumber}' is '{inv.InvoiceStatus}' (portal-owned/terminal); the ERP cannot advance it to '{newStatus}'" +
                        (inv.InvoiceStatus == InvoiceStatus.Rejected
                            ? " — a Rejected invoice's reservation was already released; re-submit it in the portal instead."
                            : " until it is posted.")));
                    continue;
                }

                if (inv.InvoiceStatus == newStatus)
                {
                    // Idempotent re-push of the same state.
                    if (!string.IsNullOrWhiteSpace(rec.ErpCode)) inv.ErpCode = rec.ErpCode!.Trim();
                    inv.UpdatedBy = "infor:inbound";
                    inv.UpdatedOn = now;
                    results.Add(new RowResult(code, RowOutcome.Skipped, null));
                    continue;
                }

                // R6 (plan D8) — leaving the reservation-holding set via an ERP rejection releases the invoice's
                // per-PO-line invoiced-qty reservation. After the guard above the current status is always one of
                // the reservation-holding states, but re-assert via HoldsReservation for symmetry with the portal
                // Reject/Revoke paths. The release's conditional ExecuteUpdates run IMMEDIATELY — inside the
                // executor's wrapping transaction (BeginTransactionAsync … Commit) — so they commit or roll back
                // ATOMICALLY with the tracked status flip below.
                if (newStatus == InvoiceStatus.Rejected && InvoiceReservationRelease.HoldsReservation(inv.InvoiceStatus))
                    await InvoiceReservationRelease.ReleaseAsync(db, inv.Id, logger, token);

                inv.InvoiceStatus = newStatus;
                if (!string.IsNullOrWhiteSpace(rec.ErpCode)) inv.ErpCode = rec.ErpCode!.Trim();
                inv.UpdatedBy = "infor:inbound";
                inv.UpdatedOn = now;
                results.Add(new RowResult(code, RowOutcome.Updated, null));
            }
            return results;
        }
    }
}
