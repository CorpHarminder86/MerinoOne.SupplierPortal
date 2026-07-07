using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R10 — ONE outbound integration = one row: connection + eligibility gate + request/response/ack mappings
/// + portal entity. Unifies the R9 Ln endpoint config (transaction posting) and the R8 IDM pair
/// (<c>IdmAttachmentTypeConfig</c> + <c>OutboundEndpointConfig</c>, folded in by migration 0051) into a single
/// config plane read by TWO executors — the config is generic, the queue disciplines are not:
///
/// <para><b><see cref="Kind"/> = Transaction</b> → the <c>OutboxMessage</c> pipeline (deterministic keys,
/// manual re-arm, sweep/backfill). One row per (tenant, <see cref="TransactionType"/>).</para>
///
/// <para><b><see cref="Kind"/> = Document</b> → the <c>IdmDocumentOutbox</c> pipeline (per-document FIFO,
/// auto-backoff, pid statefulness). One row per (tenant, <see cref="PortalEntity"/>, <see cref="AttachmentType"/>);
/// NULL <see cref="AttachmentType"/> = catch-all. Uses the extra Mutate/Delete routing columns.</para>
///
/// <para><b>Connection (R10):</b> <see cref="ConnectionPointId"/> tags the target connection (Tally, second LN
/// instance, …). NULL = the tenant's default connection point — exactly the pre-R10 behavior.</para>
///
/// <para><b>Wire formats:</b> mappings are always JSONata over JSON. <see cref="ResponseFormat"/> = Xml means
/// the transport normalizes the response body XML→JSON before <see cref="ResponseMappingExpr"/> runs (IDM).
/// <see cref="RequestFormat"/> = Xml is reserved for XML-speaking targets (classic Tally) — save-blocked until
/// the JSON→XML request serializer ships. Retriability classification stays code-owned on the HTTP status code
/// (D-R9-5) — an expression can extract data, never flip retry class.</para>
///
/// <para>Cutover/enable gates (D-R9-2/17/21) unchanged from R9: tri-state <see cref="DispatchMode"/>
/// (Document kind allows Dynamic|Held only — it has no legacy compiled builder), attestation + path
/// confirmation + fresh pinned sample before → Dynamic on Transaction rows.</para>
///
/// <para>Tenant-scoped (<see cref="ITenantOwned"/>), NOT seccode-protected (integration infrastructure,
/// same rationale as <see cref="OutboxMessage"/>). Auth/base URL live on the connection point (or the
/// tenant's Infor connection for the default), never here.</para>
/// </summary>
public class OutboundIntegrationConfig : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    /// <summary>Executor discriminator — CHECK-constrained enum name. Immutable after create.</summary>
    public OutboundIntegrationKind Kind { get; set; } = OutboundIntegrationKind.Transaction;

    /// <summary>Target connection. NULL = tenant default connection point (pre-R10 behavior).</summary>
    public Guid? ConnectionPointId { get; set; }
    public ConnectionPoint? ConnectionPoint { get; set; }

    /// <summary>Transaction kind only: outbox transaction type served, e.g. <c>InvoicePost</c> — joins <c>OutboxMessage.TransactionType</c>.</summary>
    public string? TransactionType { get; set; }

    /// <summary>
    /// Portal entity: Transaction kind = input-document root (<c>LnPortalEntity</c> constant, always resolved
    /// from THIS row, never from <c>OutboxMessage.EntityName</c>); Document kind = <c>DocumentUpload.OwnerEntityType</c>
    /// the row classifies (Asn | Invoice | Supplier).
    /// </summary>
    public string PortalEntity { get; set; } = string.Empty;

    /// <summary>Document kind only: <c>doc.AttachmentType.code</c> filter. NULL = every document of <see cref="PortalEntity"/>.</summary>
    public string? AttachmentType { get; set; }

    /// <summary>Document kind: the entity-type name on the TARGET side (was <c>idmEntityType</c>, e.g. <c>"InforInvoice"</c>) —
    /// selector for the snapshot provider and the <c>MDS_EntityType</c> payload value.</summary>
    public string? TargetEntityName { get; set; }

    /// <summary>Static context JSON surfaced to mapping expressions as <c>config.*</c> (generalizes the R8
    /// DefaultAcl/EntityName pair, e.g. <c>{"acl":"Public","entityName":"MDS_GenericDocument"}</c>).</summary>
    public string? ContextJson { get; set; }

    /// <summary>Create/post path appended to the connection's base URL (absolute http(s) URL used verbatim).</summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>HTTP verb for create/post (POST | PUT | PATCH) — CHECK-constrained. Default POST.</summary>
    public string HttpVerb { get; set; } = "POST";

    /// <summary>Document kind: routing for the Update operation. NULL path = reuse <see cref="EndpointPath"/>/<see cref="HttpVerb"/>.</summary>
    public string? MutatePath { get; set; }
    public string? MutateVerb { get; set; }

    /// <summary>Document kind: routing for the Delete operation. NULL path = reuse <see cref="EndpointPath"/> with DELETE.</summary>
    public string? DeletePath { get; set; }
    public string? DeleteVerb { get; set; }

    /// <summary>Constant headers only (JSON object). Dynamic headers come from the mapping expression (expression wins on conflict).</summary>
    public string? StaticHeadersJson { get; set; }

    /// <summary>Wire body serialization: Json | Xml (CHECK). Xml reserved until the JSON→XML request serializer ships (save-blocked).</summary>
    public string RequestFormat { get; set; } = "Json";

    /// <summary>Response body normalization before <see cref="ResponseMappingExpr"/>: Json | Xml (CHECK). Xml → generic XML→JSON normalizer (IDM).</summary>
    public string ResponseFormat { get; set; } = "Json";

    /// <summary>Tri-state cutover/kill switch (D-R9-2 + D-R9-11) — CHECK-constrained enum name. Document kind: Dynamic | Held only.</summary>
    public OutboundDispatchMode DispatchMode { get; set; } = OutboundDispatchMode.Legacy;

    /// <summary>JSONata boolean eligibility gate (strict-true, fail closed). Null/blank = no gate.</summary>
    public string? EligibilityGateExpr { get; set; }

    /// <summary>JSONata request mapping. Transaction: input document → target payload. Document: Create envelope <c>{headers, body}</c>.</summary>
    public string RequestMappingExpr { get; set; } = string.Empty;

    /// <summary>Document kind: JSONata mapping for pid-keyed Update. NULL falls back to <see cref="RequestMappingExpr"/> (create shape carrying pid).</summary>
    public string? MutateMappingExpr { get; set; }

    /// <summary>JSONata response mapping over the (normalized-to-JSON) response body. Transaction: → closed contract
    /// (D-R9-4), required to go Dynamic. Document: → <c>{pid}</c>; NULL/blank falls back to the code-owned IDM parser.</summary>
    public string? ResponseMappingExpr { get; set; }

    /// <summary>JSONata ack mapping — async ack body → closed contract. Dormant while the inbound ack shape stays code-owned.</summary>
    public string? AckMappingExpr { get; set; }

    /// <summary>Normalized SHA-256 of the seeded expressions (drift detection, hash-gate re-seed).</summary>
    public string? RequestMappingSeedHash { get; set; }
    public string? MutateMappingSeedHash { get; set; }
    public string? ResponseMappingSeedHash { get; set; }
    public string? AckMappingSeedHash { get; set; }

    /// <summary>Transaction kind: name of a code-registered candidate filter (D-R9-15) — registry-validated at save.</summary>
    public string? CandidateFilterName { get; set; }

    /// <summary>Optional JSON params for parameterized built-ins, e.g. <c>{"statuses":["Accepted"]}</c>.</summary>
    public string? CandidateFilterParams { get; set; }

    /// <summary>Monotonic gate/mapping version — bumped on any expression/filter change (backfill auto-prompt, D-R9-19).</summary>
    public int GateVersion { get; set; } = 1;

    /// <summary>Pinned input-document snapshot (D-R9-18) for save-time validation.</summary>
    public string? SampleDocumentJson { get; set; }

    /// <summary>Builder-version stamp at pin time; ≠ current builder version ⇒ "sample stale" badge.</summary>
    public string? SampleBuilderVersion { get; set; }

    /// <summary>Sample response body the response mapping is validated against at save.</summary>
    public string? ResponseSampleJson { get; set; }

    /// <summary>Sample async-ack body the ack mapping is validated against at save.</summary>
    public string? AckSampleJson { get; set; }

    /// <summary>Manual dry-post attestation evidence (D-R9-17). The system records; it does not verify.</summary>
    public string? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedNote { get; set; }

    /// <summary>D-R9-21 — hard enable gate: "endpoint path confirmed against tenant Available-APIs export".</summary>
    public bool PathConfirmed { get; set; }
}
