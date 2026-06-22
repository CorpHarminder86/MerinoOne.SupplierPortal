using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Commands;

/// <summary>
/// Retries the underlying Infor integration leg that produced an <see cref="IntegrationError"/>.
/// <list type="number">
///   <item>Loads the error + the originating <see cref="InforSyncLog"/> (when known).</item>
///   <item>Re-invokes the correct <see cref="IInforIntegrationService"/> method by
///         (<c>EntityName</c>, <c>Direction</c>).</item>
///   <item>Writes a fresh <see cref="InforSyncLog"/> row (Success or Failed) with a new idempotency key.</item>
///   <item>Resolves the error only on success. Always increments <c>RetryCount</c> and stamps <c>LastRetriedAt</c>.</item>
/// </list>
/// </summary>
public record RetryIntegrationErrorCommand(Guid ErrorId) : IRequest<Unit>;

public class RetryIntegrationErrorCommandHandler : IRequestHandler<RetryIntegrationErrorCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IInforIntegrationService _infor;
    private readonly IOutboundIdempotencyContext _idempotency;

    public RetryIntegrationErrorCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        IInforIntegrationService infor,
        IOutboundIdempotencyContext idempotency)
    {
        _db = db;
        _user = user;
        _infor = infor;
        _idempotency = idempotency;
    }

    public async Task<Unit> Handle(RetryIntegrationErrorCommand request, CancellationToken ct)
    {
        var err = await _db.IntegrationErrors.FirstOrDefaultAsync(x => x.Id == request.ErrorId, ct)
                  ?? throw new NotFoundException("IntegrationError", request.ErrorId);

        InforSyncLog? originating = null;
        if (err.SyncLogId.HasValue)
        {
            originating = await _db.InforSyncLogs.FirstOrDefaultAsync(s => s.Id == err.SyncLogId.Value, ct);
        }

        var entityName = !string.IsNullOrEmpty(err.EntityName)
            ? err.EntityName
            : originating?.EntityName ?? string.Empty;
        var direction = originating?.Direction ?? SyncDirection.Outbound;
        var payloadRef = originating?.PayloadRef; // e.g. "supplier:<guid>" — used to pick the target row

        // Deterministic key (D2 fix): replay the ORIGINATING idempotency key verbatim — NEVER mint a fresh GUID
        // — so the ERP dedupes the retry. The outbox dispatcher stamps this key on the Failed SyncLog it writes.
        var deterministicKey = originating?.IdempotencyKey;

        err.RetryCount += 1;
        err.LastRetriedAt = DateTime.UtcNow;

        InforSyncResult result;
        try
        {
            if (!string.IsNullOrEmpty(deterministicKey)) _idempotency.Set(deterministicKey);
            result = await DispatchAsync(entityName, payloadRef, ct);
        }
        catch (Exception ex)
        {
            result = new InforSyncResult(false, deterministicKey, ex.Message);
        }
        finally
        {
            _idempotency.Clear();
        }

        // EntityId: the single entity GUID encoded in the payloadRef ("<target>:<guid>"), when present.
        var entityId = TryParseGuidFromPayloadRef(payloadRef)?.ToString();

        var log = new InforSyncLog
        {
            EntityName    = entityName,
            EntityId      = entityId,
            Direction     = direction,
            Status        = result.Success ? SyncStatus.Success : SyncStatus.Failed,
            PayloadRef    = payloadRef,
            RetryCount    = err.RetryCount,
            // Reuse the deterministic key on the retry log row too, so retry history stays correlatable.
            IdempotencyKey = result.IdempotencyKey ?? deterministicKey ?? Guid.NewGuid().ToString("N"),
            SyncedAt      = DateTime.UtcNow,
            ErrorMessage  = result.Success ? null : result.Message
        };
        _db.InforSyncLogs.Add(log);

        if (result.Success)
        {
            err.IsResolved = true;
            err.ResolutionNote = $"Retried successfully at {DateTime.UtcNow:O} by {_user.UserCode}";
        }
        else
        {
            err.IsResolved = false;
            var detail = string.IsNullOrEmpty(result.Message) ? "unknown failure" : result.Message;
            err.ResolutionNote = $"Retry {err.RetryCount} failed at {DateTime.UtcNow:O} by {_user.UserCode}: {detail}";
        }

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }

    /// <summary>
    /// Maps (<paramref name="entityName"/>, <paramref name="payloadRef"/>) to the right
    /// <see cref="IInforIntegrationService"/> call. <paramref name="payloadRef"/> follows the
    /// convention "&lt;target&gt;:&lt;guid&gt;" set by mock outbound writers. Falls back to a
    /// best-effort retry when payloadRef is missing or malformed.
    /// </summary>
    private async Task<InforSyncResult> DispatchAsync(string entityName, string? payloadRef, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entityName))
            return new InforSyncResult(false, null, "Cannot retry: original entity name is unknown.");

        var targetId = TryParseGuidFromPayloadRef(payloadRef);

        return (entityName, targetId) switch
        {
            ("Supplier", Guid id)        => await _infor.SyncSupplierAsync(id, ct),
            ("PurchaseOrder", Guid id)   => await _infor.AcknowledgePurchaseOrderAsync(id, ct),
            ("Invoice", Guid id)         => await _infor.SubmitInvoiceAsync(id, ct),
            ("Asn", Guid id)             => await _infor.SubmitAsnAsync(id, ct),
            ("SupplierChange", Guid id)  => await _infor.SubmitSupplierChangeAsync(id, ct),
            _                            => new InforSyncResult(false, null,
                                              $"No retry handler for entity '{entityName}'" +
                                              (payloadRef is null ? " (no payloadRef)." : "."))
        };
    }

    private static Guid? TryParseGuidFromPayloadRef(string? payloadRef)
    {
        if (string.IsNullOrEmpty(payloadRef)) return null;
        var idx = payloadRef.IndexOf(':');
        var tail = idx >= 0 ? payloadRef[(idx + 1)..] : payloadRef;
        return Guid.TryParse(tail, out var g) ? g : null;
    }
}
