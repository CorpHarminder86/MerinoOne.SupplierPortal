using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.Ln.Backfill;
using MerinoOne.SupplierPortal.Application.Integration.Ln.Commands;
using MerinoOne.SupplierPortal.Application.Integration.Ln.Monitor;
using MerinoOne.SupplierPortal.Application.Integration.Switches;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R9 §2.1 / R10 — unified outbound-integration admin: config CRUD for BOTH kinds (Transaction = LN posting,
/// Document = IDM sync), save-time JSONata/contract validation, sample pin (D-R9-18), attestation + path
/// confirmation (D-R9-17/21), the tri-state dispatch-mode switch (D-R9-2/11), and connection points.
/// Reads are Settings.Read; every mutation is the high-blast-radius Integration.Admin.
/// The route keeps its R9 name (api/integration/ln-outbound) as a stable alias; api/integration/outbound is
/// the R10 canonical.
/// </summary>
[ApiController]
[Authorize]
[Route("api/integration/outbound")]
[Route("api/integration/ln-outbound")]
public class LnOutboundConfigController : ControllerBase
{
    private readonly IMediator _mediator;
    public LnOutboundConfigController(IMediator mediator) => _mediator = mediator;

    [HttpGet("configs")]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("List outbound integration configs")]
    [EndpointDescription("The tenant's unified outbound integration configs (Transaction + Document kinds) with connection tags, drift flags, sample state (stale badge on builder-version drift) and attestation evidence. Requires Settings.Read.")]
    public async Task<Result<IReadOnlyList<OutboundIntegrationConfigDto>>> GetConfigs(CancellationToken ct)
        => Result<IReadOnlyList<OutboundIntegrationConfigDto>>.Ok(await _mediator.Send(new GetOutboundIntegrationConfigsQuery(), ct), HttpContext.TraceIdentifier);

    // ── R10 — connection points ───────────────────────────────────────────────────────────────────────
    [HttpGet("connection-points")]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("List connection points")]
    [EndpointDescription("The tenant's named outbound connection targets (default chip, in-use counts, transport availability). Auth blobs are never returned. Requires Settings.Read.")]
    public async Task<Result<IReadOnlyList<ConnectionPointDto>>> GetConnectionPoints(CancellationToken ct)
        => Result<IReadOnlyList<ConnectionPointDto>>.Ok(await _mediator.Send(new Application.Integration.Connection.GetConnectionPointsQuery(), ct), HttpContext.TraceIdentifier);

    [HttpPost("connection-points")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Create/update a connection point")]
    [EndpointDescription("Upserts a connection point. InforION rows carry no URL/auth (resolved from the Infor connection settings); other types require a base URL and store their auth blob encrypted. Requires Integration.Admin.")]
    public async Task<Result<Guid>> SaveConnectionPoint([FromBody] SaveConnectionPointRequest body, CancellationToken ct)
        => Result<Guid>.Ok(await _mediator.Send(new Application.Integration.Connection.SaveConnectionPointCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPost("connection-points/{id:guid}/set-default")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Set the tenant default connection")]
    [EndpointDescription("Moves the tenant default (what NULL-tagged config rows dispatch through). Blocked for connection types with no registered transport. Requires Integration.Admin.")]
    public async Task<Result<bool>> SetDefaultConnectionPoint(Guid id, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new Application.Integration.Connection.SetDefaultConnectionPointCommand(id), ct), HttpContext.TraceIdentifier);

    [HttpDelete("connection-points/{id:guid}")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Delete a connection point")]
    [EndpointDescription("Soft-deletes a connection point. Blocked while it is the default or any integration config is tagged to it. Requires Integration.Admin.")]
    public async Task<Result<bool>> DeleteConnectionPoint(Guid id, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new Application.Integration.Connection.DeleteConnectionPointCommand(id), ct), HttpContext.TraceIdentifier);

    [HttpGet("candidate-filters")]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("List code-registered candidate filters")]
    [EndpointDescription("The (portalEntity, name) candidate filters discoverable via the [CandidateFilter] registry (D-R9-15) — the config dropdown's only source; free-text SQL is banned. Requires Settings.Read.")]
    public async Task<Result<IReadOnlyList<LnCandidateFilterDto>>> GetCandidateFilters([FromQuery] string? portalEntity, CancellationToken ct)
        => Result<IReadOnlyList<LnCandidateFilterDto>>.Ok(await _mediator.Send(new GetLnCandidateFiltersQuery(portalEntity), ct), HttpContext.TraceIdentifier);

    [HttpGet("sample-candidates")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Search entities for the sample-pin picker")]
    [EndpointDescription("RLS-scoped recent entities of the given portalEntity (pick a representative one — multi-line, populated lots/serials — per TSD §2.9). Requires Integration.Admin.")]
    public async Task<Result<IReadOnlyList<LnSampleCandidateDto>>> SearchSampleCandidates([FromQuery] string portalEntity, [FromQuery] string? search, CancellationToken ct)
        => Result<IReadOnlyList<LnSampleCandidateDto>>.Ok(await _mediator.Send(new SearchLnSampleCandidatesQuery(portalEntity, search), ct), HttpContext.TraceIdentifier);

    [HttpPost("configs")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Create/update an LN endpoint config")]
    [EndpointDescription("Upserts (by id, else tenant+transactionType) after the full save-time pipeline: expressions compile, gate is boolean vs the pinned sample, response/ack satisfy the CLOSED contract (unknown keys block), candidateFilterName resolves in the registry. Any gate/mapping/filter change bumps gateVersion. Never changes dispatchMode. Requires Integration.Admin.")]
    public async Task<Result<Guid>> SaveConfig([FromBody] SaveOutboundIntegrationConfigRequest body, CancellationToken ct)
        => Result<Guid>.Ok(await _mediator.Send(new SaveOutboundIntegrationConfigCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPost("configs/validate")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Dry-validate a config shape")]
    [EndpointDescription("Runs the save-time pipeline without writing: per-slot errors + the rendered request preview against the supplied sample. (The Phase D mapping editor's live-eval endpoint.) Requires Integration.Admin.")]
    public async Task<Result<LnConfigValidationResultDto>> Validate([FromBody] ValidateOutboundIntegrationConfigRequest body, CancellationToken ct)
        => Result<LnConfigValidationResultDto>.Ok(await _mediator.Send(new ValidateOutboundIntegrationConfigCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPost("configs/{id:guid}/pin-sample")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Pin a sample document")]
    [EndpointDescription("Runs the chosen REAL entity through the actual input-document builder and freezes the output + builder-version stamp on the config (D-R9-18 — hand-authored samples drift and rot validation). Requires Integration.Admin.")]
    public async Task<Result<bool>> PinSample(Guid id, [FromBody] PinLnSampleRequest body, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new PinLnSampleDocumentCommand(id, body.EntityId), ct), HttpContext.TraceIdentifier);

    [HttpPost("configs/{id:guid}/attest")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Record the manual dry-post attestation")]
    [EndpointDescription("Stamps verifiedBy/At/Note (D-R9-17 — the system records, it does not verify). pathConfirmed=true is the D-R9-21 confirm checkbox ('endpoint path confirmed against tenant Available-APIs export') and is stamped into the note; it hard-gates dispatchMode=Dynamic. Requires Integration.Admin.")]
    public async Task<Result<bool>> Attest(Guid id, [FromBody] AttestLnEndpointRequest body, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new AttestLnEndpointCommand(id, body), ct), HttpContext.TraceIdentifier);

    [HttpPost("configs/{id:guid}/dispatch-mode")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Switch dispatch mode (Legacy | Dynamic | Held)")]
    [EndpointDescription("Tri-state cutover/kill: → Dynamic requires attestation + pathConfirmed + a fresh pinned sample + green validation; → Held (per-endpoint kill — rows accumulate Pending, enqueue continues) and → Legacy (rollback) are never blocked. Requires Integration.Admin.")]
    public async Task<Result<bool>> SetDispatchMode(Guid id, [FromBody] SetOutboundDispatchModeRequest body, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new SetOutboundDispatchModeCommand(id, body.Mode), ct), HttpContext.TraceIdentifier);

    [HttpPost("configs/{id:guid}/restore-default")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Restore an expression slot to the repo default")]
    [EndpointDescription("Overwrites one slot (request | response | ack) from the source-controlled default and re-stamps its seed hash (bumps gateVersion). Requires Integration.Admin.")]
    public async Task<Result<bool>> RestoreDefault(Guid id, [FromBody] RestoreLnDefaultExpressionRequest body, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new RestoreLnDefaultExpressionCommand(id, body.Slot), ct), HttpContext.TraceIdentifier);

    [HttpDelete("configs/{id:guid}")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Delete an LN endpoint config")]
    [EndpointDescription("Soft delete = permanent rollback to the legacy compiled builder for this transaction type (D-R9-2). Requires Integration.Admin.")]
    public async Task<Result<bool>> Delete(Guid id, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new DeleteOutboundIntegrationConfigCommand(id), ct), HttpContext.TraceIdentifier);

    // ── Phase B — outbox monitor, kill switches, backfill, held inbound (reads on Integration.Read per the
    // existing SyncLog/Errors precedent; every mutation stays Integration.Admin — O-R9-6 close-out). ─────────

    [HttpGet("outbox")]
    [Authorize(Policy = Perm.IntegrationRead)]
    [EndpointSummary("Page the LN outbox")]
    [EndpointDescription("Paged outbox rows with the R9 columns: Skipped rows carry skipReason + gateVersion (a skip is a decision — this is its surface, D-R9-9); Failed rows carry the Permanent|Retriable errorClass. Requires Integration.Read.")]
    public async Task<Result<OutboxMessagePageDto>> GetOutbox([FromQuery] string? status, [FromQuery] string? transactionType, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => Result<OutboxMessagePageDto>.Ok(await _mediator.Send(new GetOutboxMessagesQuery(status, transactionType, page, pageSize), ct), HttpContext.TraceIdentifier);

    [HttpPost("outbox/{id:guid}/rearm")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Re-arm a Skipped/Failed outbox row")]
    [EndpointDescription("Flips Skipped/Failed → Pending in place (same deterministic key — LN dedupes). Allowed on Permanent-classified failures as an admin override; the result flags it so the UI warns. Requires Integration.Admin.")]
    public async Task<Result<RearmOutboxResultDto>> RearmOutbox(Guid id, CancellationToken ct)
        => Result<RearmOutboxResultDto>.Ok(await _mediator.Send(new RearmOutboxMessageCommand(id), ct), HttpContext.TraceIdentifier);

    [HttpGet("switches")]
    [Authorize(Policy = Perm.IntegrationRead)]
    [EndpointSummary("Kill-switch states")]
    [EndpointDescription("The two DB-backed scopes (OutboundGlobal, InboundErpAck) with last reason/who/when and the held-ack count. Absent row = enabled. Per-endpoint kill lives on the config's dispatchMode=Held. Requires Integration.Read.")]
    public async Task<Result<IReadOnlyList<IntegrationSwitchDto>>> GetSwitches(CancellationToken ct)
        => Result<IReadOnlyList<IntegrationSwitchDto>>.Ok(await _mediator.Send(new GetIntegrationSwitchesQuery(), ct), HttpContext.TraceIdentifier);

    [HttpPost("switches/{scope}/toggle")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Toggle a kill switch")]
    [EndpointDescription("D-R9-11: OutboundGlobal stops dispatch (never enqueue — rows accumulate Pending and drain FIFO on re-enable); InboundErpAck makes /inbound/erp-ack accept-and-hold (never 503; held acks replay on re-enable). Reason note is MANDATORY; every toggle is audited. Requires Integration.Admin.")]
    public async Task<Result<bool>> ToggleSwitch(string scope, [FromBody] ToggleIntegrationSwitchRequest body, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new ToggleIntegrationSwitchCommand(scope, body), ct), HttpContext.TraceIdentifier);

    [HttpGet("switches/audit")]
    [Authorize(Policy = Perm.IntegrationRead)]
    [EndpointSummary("Kill-switch toggle audit")]
    [EndpointDescription("Who/when/old→new/reason per toggle, newest first. Requires Integration.Read.")]
    public async Task<Result<IReadOnlyList<IntegrationSwitchAuditDto>>> GetSwitchAudit([FromQuery] string? scope, CancellationToken ct)
        => Result<IReadOnlyList<IntegrationSwitchAuditDto>>.Ok(await _mediator.Send(new GetIntegrationSwitchAuditQuery(scope), ct), HttpContext.TraceIdentifier);

    [HttpGet("held-inbound")]
    [Authorize(Policy = Perm.IntegrationRead)]
    [EndpointSummary("Held inbound erp-acks")]
    [EndpointDescription("The accept-and-hold store: acks received under an InboundErpAck kill, replayed FIFO on re-enable (5-attempt cap, then Failed + IntegrationError). Requires Integration.Read.")]
    public async Task<Result<IReadOnlyList<HeldInboundMessageDto>>> GetHeldInbound([FromQuery] string? status, CancellationToken ct)
        => Result<IReadOnlyList<HeldInboundMessageDto>>.Ok(await _mediator.Send(new GetHeldInboundMessagesQuery(status), ct), HttpContext.TraceIdentifier);

    [HttpGet("backfill/{configId:guid}/status")]
    [Authorize(Policy = Perm.IntegrationRead)]
    [EndpointSummary("Backfill status for a config")]
    [EndpointDescription("Drives the D-R9-19 auto-prompt: promptDryRun=true when the gateVersion moved past the last applied run and no fresh preview exists. Apply is ALWAYS manual. Requires Integration.Read.")]
    public async Task<Result<LnBackfillStatusDto>> GetBackfillStatus(Guid configId, CancellationToken ct)
        => Result<LnBackfillStatusDto>.Ok(await _mediator.Send(new GetLnBackfillStatusQuery(configId), ct), HttpContext.TraceIdentifier);

    [HttpPost("backfill/{configId:guid}/dry-run")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Run a backfill dry-run")]
    [EndpointDescription("MANDATORY preview (D-R9-10): entity scan (shared scanner) + per-row outbox re-evaluation → enqueue / re-arm / withdraw sets with row lists; Sending informational; posted rows immutable. Persisted gateVersion-pinned for the apply. Requires Integration.Admin.")]
    public async Task<Result<LnBackfillPreviewDto>> RunBackfillDryRun(Guid configId, CancellationToken ct)
        => Result<LnBackfillPreviewDto>.Ok(await _mediator.Send(new RunLnBackfillDryRunCommand(configId), ct), HttpContext.TraceIdentifier);

    [HttpGet("backfill/runs")]
    [Authorize(Policy = Perm.IntegrationRead)]
    [EndpointSummary("Backfill run history")]
    public async Task<Result<IReadOnlyList<LnBackfillRunDto>>> GetBackfillRuns([FromQuery] Guid? configId, CancellationToken ct)
        => Result<IReadOnlyList<LnBackfillRunDto>>.Ok(await _mediator.Send(new GetLnBackfillRunsQuery(configId), ct), HttpContext.TraceIdentifier);

    [HttpGet("backfill/runs/{runId:guid}")]
    [Authorize(Policy = Perm.IntegrationRead)]
    [EndpointSummary("A backfill run's stored preview")]
    public async Task<Result<LnBackfillPreviewDto>> GetBackfillRun(Guid runId, CancellationToken ct)
        => Result<LnBackfillPreviewDto>.Ok(await _mediator.Send(new GetLnBackfillRunQuery(runId), ct), HttpContext.TraceIdentifier);

    [HttpPost("backfill/runs/{runId:guid}/apply")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Apply a previewed backfill")]
    [EndpointDescription("Deliberate, never one-click: refuses a stale gateVersion (re-run the dry-run). Conditional status-guarded updates make dispatcher races VISIBLE (racedAway / escapedToSending — the dispatch re-check resolves escapes). Requires Integration.Admin.")]
    public async Task<Result<LnBackfillApplyResultDto>> ApplyBackfill(Guid runId, CancellationToken ct)
        => Result<LnBackfillApplyResultDto>.Ok(await _mediator.Send(new ApplyLnBackfillCommand(runId), ct), HttpContext.TraceIdentifier);
}
