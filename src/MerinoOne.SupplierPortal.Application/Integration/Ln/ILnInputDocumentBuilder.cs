using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Application.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.2, D-R9-3/D-R9-7) — builds the frozen input document for one portalEntity: the canonical
/// source JSON that eligibility gates and request mappings evaluate against (and the shape the Phase D
/// picker reflects). Loads with <c>IgnoreQueryFilters()</c> + explicit soft-delete guards (background
/// dispatcher scope has no ambient tenant) and TRACKED root queries — at enqueue time the gate must see
/// the caller's in-flight mutations via EF identity resolution, exactly like the legacy payload builders.
/// </summary>
public interface ILnInputDocumentBuilder
{
    /// <summary>The <c>LnPortalEntity</c> constant this builder serves. Resolved from the CONFIG row, never from OutboxMessage.EntityName.</summary>
    string PortalEntity { get; }

    /// <summary>Current <c>LnInputDocumentVersions</c> stamp — pinned onto samples; drift renders the stale badge (D-R9-18).</summary>
    string BuilderVersion { get; }

    /// <summary>
    /// Builds the input-document JSON (nulls kept), or null when the entity does not exist / is deleted.
    /// <paramref name="transactionType"/> + <paramref name="outboxPayloadJson"/> feed per-row context —
    /// today only the PurchaseOrder builder uses them (responseContext: action / proposedDate / reason,
    /// historically carried on <c>OutboxMessage.PayloadJson</c>).
    /// </summary>
    Task<string?> BuildJsonAsync(IAppDbContext db, Guid entityId, string transactionType, string? outboxPayloadJson, CancellationToken ct = default);
}

/// <summary>Startup-composed lookup <c>portalEntity → builder</c> (mirrors the R8 snapshot-provider registry).</summary>
public interface ILnInputDocumentBuilderRegistry
{
    ILnInputDocumentBuilder? TryGet(string portalEntity);
    IReadOnlyCollection<ILnInputDocumentBuilder> All { get; }
}
