using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R9 (TSD R9 §2.1) — per-transaction-type LN outbound endpoint configuration: the config layer that
/// replaces the compiled payload builders and <c>EndpointPaths</c> constants. One row per
/// (tenant, <see cref="TransactionType"/>). Integration infrastructure — tenant-scoped
/// (<see cref="ITenantOwned"/>), NOT seccode-protected (same rationale as <see cref="OutboxMessage"/>).
///
/// <para><b>Cutover (D-R9-2, tri-state):</b> the dispatcher resolves this row by transaction type each
/// drain cycle. <see cref="DispatchMode"/> = <c>Legacy</c> → the compiled builder serves exactly as
/// before (rows seed Legacy; creating a row changes nothing); <c>Dynamic</c> → input-document builder +
/// JSONata request/response mapping; <c>Held</c> → per-endpoint kill (rows stay Pending, enqueue
/// continues, D-R9-11). Soft-deleting the row = permanent fallback to legacy.</para>
///
/// <para><b>Enable gate (D-R9-17 + D-R9-21):</b> → Dynamic is blocked until attestation
/// (<see cref="VerifiedBy"/>/<see cref="VerifiedAt"/>/<see cref="VerifiedNote"/>) is recorded AND
/// <see cref="PathConfirmed"/> is ticked AND a fresh pinned sample exists AND the full validation
/// pipeline is green. The system records the attestation; it does not verify it.</para>
///
/// <para>Auth and base URL deliberately absent (R8 D3 precedent): resolved per tenant from
/// <see cref="InforConnectionSetting"/>; <see cref="EndpointPath"/> is appended at send time.</para>
/// </summary>
public class LnEndpointConfig : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    /// <summary>Outbox transaction type this config serves, e.g. <c>InvoicePost</c> — joins to <c>OutboxMessage.TransactionType</c>.</summary>
    public string TransactionType { get; set; } = string.Empty;

    /// <summary>
    /// Input-document root (<c>LnPortalEntity</c> constant, e.g. <c>Invoice</c>). Always resolved from THIS row —
    /// never inferred from <c>OutboxMessage.EntityName</c> (PoNegotiationApprove rows carry EntityName=PurchaseOrder
    /// with a negotiation EntityId).
    /// </summary>
    public string PortalEntity { get; set; } = string.Empty;

    /// <summary>Relative ION path appended to the tenant's <see cref="InforConnectionSetting.ApiBaseUrl"/>.</summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>HTTP verb (POST | PUT | PATCH) — CHECK-constrained. Default POST.</summary>
    public string HttpVerb { get; set; } = "POST";

    /// <summary>Tri-state cutover/kill switch (D-R9-2 + D-R9-11) — CHECK-constrained enum name.</summary>
    public LnDispatchMode DispatchMode { get; set; } = LnDispatchMode.Legacy;

    /// <summary>JSONata boolean eligibility gate (D-R9-6). Null/blank = no gate. Dormant until Phase B wires evaluation.</summary>
    public string? EligibilityGateExpr { get; set; }

    /// <summary>JSONata request mapping — input document → free-form LN payload (LN owns the shape).</summary>
    public string RequestMappingExpr { get; set; } = string.Empty;

    /// <summary>JSONata response mapping — synchronous LN response body → closed contract (D-R9-4).</summary>
    public string ResponseMappingExpr { get; set; } = string.Empty;

    /// <summary>JSONata ack mapping — /inbound/erp-ack body → closed contract. Dormant while the inbound ack shape stays code-owned.</summary>
    public string? AckMappingExpr { get; set; }

    /// <summary>Normalized SHA-256 of the seeded request expression (drift detection, IDM hash-gate precedent).</summary>
    public string? RequestMappingSeedHash { get; set; }
    public string? ResponseMappingSeedHash { get; set; }
    public string? AckMappingSeedHash { get; set; }

    /// <summary>Name of a code-registered candidate filter (D-R9-15) — registry-validated at save; free-text SQL banned.</summary>
    public string? CandidateFilterName { get; set; }

    /// <summary>Optional JSON params for parameterized built-ins, e.g. <c>{"statuses":["Accepted"]}</c> for <c>StatusIn</c>.</summary>
    public string? CandidateFilterParams { get; set; }

    /// <summary>Monotonic gate/mapping version — bumped by the save handler on any expression/filter change (drives backfill auto-prompt, D-R9-19).</summary>
    public int GateVersion { get; set; } = 1;

    /// <summary>Pinned input-document snapshot (D-R9-18): a real entity run through the builder, frozen for save-time validation.</summary>
    public string? SampleDocumentJson { get; set; }

    /// <summary>Builder-version stamp at pin time; ≠ current <c>LnInputDocumentVersions</c> ⇒ "sample stale — re-snapshot" badge.</summary>
    public string? SampleBuilderVersion { get; set; }

    /// <summary>Sample LN response body the response mapping is validated against at save (seeded with a generic OData created-entity example).</summary>
    public string? ResponseSampleJson { get; set; }

    /// <summary>Sample /inbound/erp-ack body the ack mapping is validated against at save (seeded from our own inbound ack shape).</summary>
    public string? AckSampleJson { get; set; }

    /// <summary>Manual dry-post attestation evidence (D-R9-17). The system records; it does not verify.</summary>
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedNote { get; set; }

    /// <summary>
    /// D-R9-21 — hard enable gate: "endpoint path confirmed against tenant Available-APIs export".
    /// Set only via the dedicated confirm checkbox (which also stamps the line into <see cref="VerifiedNote"/>);
    /// → Dynamic is blocked while false. Makes the wrong-path risk (O-R9-2) unskippable at enable.
    /// </summary>
    public bool PathConfirmed { get; set; }
}
