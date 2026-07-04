using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §3.5. One row = one IDM operation (Create/Update/Delete) for one
/// <see cref="DocumentUpload"/> document. <see cref="DocumentUploadId"/> is the FIFO partition key; ordering
/// within a partition is by the two-key <c>Seq</c> (ASC). Unlike the existing <see cref="OutboxMessage"/>
/// (dispatch-on-insert, global FIFO) this outbox inserts <c>Blocked</c>, promotes to <c>Pending</c> on gate
/// satisfaction, and drains per-partition (D-R8-2/3/4).
///
/// <para><b>RLS:</b> derives <see cref="BaseAggregateRoot"/> so it carries the seccode + tenant + company +
/// soft-delete envelope; <see cref="Common.ISeccode.SeccodeId"/>/tenant/company are COPIED from the owning
/// <see cref="DocumentUpload"/> row at enqueue so the sync-log query is scoped for free (spec §3.6 SQL RLS
/// policy is replaced by the EF query-filter stack). <c>isDeleted</c> doubles as the reap flag (D-R8-6).</para>
///
/// <para><b>Idempotency:</b> <see cref="CorrelationId"/> is the create idempotency handle; <see cref="ExternalId"/>
/// (the IDM pid) drives Update/Delete. A retried delete on a gone pid is a no-op Success (D-R8-5).</para>
/// </summary>
public class IdmDocumentOutbox : BaseAggregateRoot
{
    /// <summary>FK → <c>doc.DocumentUpload.documentUploadId</c> (GUID). The per-document FIFO partition key.</summary>
    public Guid DocumentUploadId { get; set; }
    public DocumentUpload? DocumentUpload { get; set; }

    /// <summary>IDM entity type discriminator (config selector + <c>MDS_EntityType</c> payload value).</summary>
    public string IdmEntityType { get; set; } = string.Empty;

    /// <summary>Owning entity id (Invoice/Asn) — denormalized from the document for gate evaluation + snapshot fetch.</summary>
    public Guid OwnerEntityId { get; set; }

    /// <summary>Filename snapshot copied from the document at enqueue (snapshot-on-write): survives the document's
    /// soft-delete so Delete-operation rows still render in the sync log (whose DocumentUpload is soft-deleted).</summary>
    public string FileName { get; set; } = string.Empty;

    public IdmOutboxOperation Operation { get; set; }

    public IdmOutboxStatus Status { get; set; } = IdmOutboxStatus.Blocked;

    /// <summary>Create idempotency handle (correlationId) sent to IDM; stable across retries.</summary>
    public Guid CorrelationId { get; set; } = Guid.NewGuid();

    /// <summary>IDM item pid — NULL on create; written on first success and echoed on Update/Delete.</summary>
    public string? ExternalId { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Earliest next dispatch time (backoff schedule). NULL = eligible now.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>Request envelope JSON with the base64 file content ELIDED (D-R8-18) — never persist the file bytes.</summary>
    public string? RequestSnapshotJson { get; set; }

    /// <summary>Raw IDM response — XML for both success and error (D-R8-21); name kept for continuity, content is XML.</summary>
    public string? ResponseJson { get; set; }

    public string? LastError { get; set; }
}
