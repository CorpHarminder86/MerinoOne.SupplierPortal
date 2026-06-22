using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Commands;

/// <summary>
/// Reviewer approves a change request: applies every delta line onto the LIVE supplier data via the typed
/// per-target appliers, flips the request to <see cref="ChangeRequestStatus.Approved"/>, then (post-commit)
/// enqueues the per-line ERP push. Gated on <c>Supplier.ApproveChange</c>.
///
/// <para><b>Atomicity:</b> the deltas + the status flip commit in ONE transaction (a single
/// <c>SaveChangesAsync</c>). If any applier throws (e.g. a bad payload, a missing target row), nothing is
/// persisted — the request stays Submitted/UnderReview and the error surfaces (400/404/409).</para>
///
/// <para><b>Optimistic concurrency (plan §4):</b> <c>SupplierChangeRequest</c> carries a <c>RowVersion</c>. Two
/// reviewers approving the same request concurrently produce a <see cref="DbUpdateConcurrencyException"/> on the
/// loser's SaveChanges — we map it to a 409 and SKIP (the deltas were already applied by the winner; re-applying
/// would double-insert Adds). This is the concurrent-approve "skip + log" the plan calls for.</para>
///
/// <para><b>Push is post-commit:</b> the ERP push runs AFTER the approve transaction commits, through the
/// Increment-0 outbox (deterministic key), and rolls the request up to Pushed / PartiallyPushed / PushFailed. A
/// push failure does NOT roll back the applied portal deltas (the portal is the supplier-master authority) — it is
/// recorded as a retryable IntegrationError + a reconciliation signal.</para>
/// </summary>
public record ApproveSupplierChangeRequestCommand(Guid Id) : IRequest<Unit>;

public class ApproveSupplierChangeRequestCommandHandler : IRequestHandler<ApproveSupplierChangeRequestCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierChangeApplier _applier;
    private readonly SupplierChangePushService _push;
    private readonly ILogger<ApproveSupplierChangeRequestCommandHandler> _logger;

    public ApproveSupplierChangeRequestCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        SupplierChangeApplier applier,
        SupplierChangePushService push,
        ILogger<ApproveSupplierChangeRequestCommandHandler> logger)
    {
        _db = db; _user = user; _applier = applier; _push = push; _logger = logger;
    }

    public async Task<Unit> Handle(ApproveSupplierChangeRequestCommand request, CancellationToken ct)
    {
        var entity = await _db.SupplierChangeRequests
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == request.Id, ct)
            ?? throw new NotFoundException("SupplierChangeRequest", request.Id);

        if (entity.ChangeStatus is not (ChangeRequestStatus.Submitted or ChangeRequestStatus.UnderReview))
            throw new ConflictException($"Only a Submitted or UnderReview request can be approved (current: {entity.ChangeStatus}).");

        var lines = entity.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.CreatedOn).ToList();
        if (lines.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "There are no changes to apply." }
            });

        // The live supplier aggregate — tracked so scalar Supplier-level edits mutate it directly.
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == entity.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", entity.SupplierId);

        var now = DateTime.UtcNow;

        // Apply every delta onto the live rows (tracked, NOT yet saved).
        foreach (var line in lines)
            await _applier.ApplyLineAsync(supplier, line, now, ct);

        // Flip the request to Approved in the SAME transaction as the applied deltas.
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
        entity.ChangeStatus = ChangeRequestStatus.Approved;
        entity.ReviewedBy = actor;
        entity.ReviewedAt = now;
        entity.UpdatedBy = actor;
        entity.UpdatedOn = now;

        try
        {
            await _db.SaveChangesAsync(ct);   // deltas + status flip — one atomic commit (RowVersion guards concurrency).
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent approve already applied the deltas. Skip + log (re-applying would double-insert Adds).
            _logger.LogWarning(
                "[SupplierChange] Concurrent approve detected for change request {RequestId} — skipping (already applied by another reviewer).",
                entity.Id);
            throw new ConflictException("This change request was approved concurrently by another reviewer; no further action was taken.");
        }

        // POST-COMMIT: enqueue the per-line ERP push (deterministic key) + roll the request up. A push failure does
        // not roll back the applied portal deltas — it is recorded as retryable + a reconciliation signal.
        var businessKey = $"{entity.SupplierId:N}|{entity.Id:N}";
        await _push.PushAsync(entity, businessKey, ct);

        return Unit.Value;
    }
}
