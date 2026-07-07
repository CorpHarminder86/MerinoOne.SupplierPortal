using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.Idm.Commands;
using MerinoOne.SupplierPortal.Application.Integration.Idm.Queries;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §7. Infor IDM document-sync: per-type mapping/gate config (Settings), transport
/// endpoints + validate/test bench (Integration), and the RLS-scoped sync log with retry/re-push. All actions are
/// portal-JWT (not X-APIKey), so they are NOT listed in the inbound IntegrationCatalog.
/// </summary>
[ApiController]
[Authorize]
[Route("api/integration/idm")]
public class IdmIntegrationController : ControllerBase
{
    private readonly IMediator _mediator;
    public IdmIntegrationController(IMediator mediator) => _mediator = mediator;

    // ── Document-integration tooling ──────────────────────────────────────────────────────────────────
    // R10: config CRUD moved to api/integration/ln-outbound/configs (unified OutboundIntegrationConfig,
    // kind=Document). What stays here: entity-type options, repo-default restore, backfill, connection
    // validate and the test bench — plus the RLS-scoped sync log.
    [HttpGet("entity-types")]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("List portal-entity → IDM entity-type pairs for a new mapping")]
    [EndpointDescription("The (portal entity, IDM entity type) pairs with a registered snapshot provider (today: Asn → InforAdvanceShipmentNoticeSupplierASN, Invoice → InforInvoice) — the only targets a NEW Document integration can dispatch. Requires Settings.Read.")]
    public async Task<Result<IReadOnlyList<IdmEntityTypeOptionDto>>> GetEntityTypeOptions(CancellationToken ct)
        => Result<IReadOnlyList<IdmEntityTypeOptionDto>>.Ok(await _mediator.Send(new GetIdmEntityTypeOptionsQuery(), ct), HttpContext.TraceIdentifier);

    [HttpPost("attachment-type-configs/{id:guid}/restore-default")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Restore an IDM mapping expression to the repo default")]
    [EndpointDescription("Overwrites a Document integration's request/mutate expressions from the source-controlled default and re-stamps the seed hash (D6). Requires Settings.Write.")]
    public async Task<Result<bool>> RestoreDefault(Guid id, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new RestoreIdmDefaultExpressionCommand(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("attachment-type-configs/backfill")]
    [Authorize(Policy = Perm.IntegrationIdmSyncManage)]
    [EndpointSummary("Backfill DocumentUpload.idmEntityType")]
    [EndpointDescription("Stamps idmEntityType on existing documents whose type maps to an active (Dynamic) Document integration and are not yet classified. Returns the updated count. Requires Integration.IdmSync.Manage.")]
    public async Task<Result<int>> Backfill(CancellationToken ct)
        => Result<int>.Ok(await _mediator.Send(new BackfillIdmEntityTypeCommand(), ct), HttpContext.TraceIdentifier);

    [HttpPost("endpoints/{id:guid}/validate")]
    [Authorize(Policy = Perm.IntegrationManage)]
    [EndpointSummary("Validate an outbound integration's connection")]
    [EndpointDescription("Checks the tenant OAuth token and reports the resolved target URL for a unified integration config row (flags a suspicious /LN-suffixed base URL for Document integrations). Requires Integration.Manage.")]
    public async Task<Result<ValidateOutboundEndpointResultDto>> ValidateEndpoint(Guid id, CancellationToken ct)
        => Result<ValidateOutboundEndpointResultDto>.Ok(await _mediator.Send(new ValidateOutboundEndpointCommand(id), ct), HttpContext.TraceIdentifier);

    [HttpGet("test-bench/documents")]
    [Authorize(Policy = Perm.IntegrationManage)]
    [EndpointSummary("Search documents for the IDM test bench")]
    [EndpointDescription("RLS-scoped document search (by filename/type) for the mapping test bench. Requires Integration.Manage.")]
    public async Task<Result<IReadOnlyList<IdmDocumentPickDto>>> SearchTestDocuments([FromQuery] string? search, CancellationToken ct)
        => Result<IReadOnlyList<IdmDocumentPickDto>>.Ok(await _mediator.Send(new SearchIdmTestDocumentsQuery(search), ct), HttpContext.TraceIdentifier);

    [HttpPost("test-bench")]
    [Authorize(Policy = Perm.IntegrationManage)]
    [EndpointSummary("Render the IDM envelope for a sample document")]
    [EndpointDescription("Assembles the snapshot for a chosen document, renders the exact Create envelope (base64 elided), reports gate satisfaction, and optionally dry-run POSTs the real payload. Requires Integration.Manage.")]
    public async Task<Result<IdmTestBenchResultDto>> TestBench([FromBody] IdmTestBenchRequest body, CancellationToken ct)
        => Result<IdmTestBenchResultDto>.Ok(await _mediator.Send(new TestIdmEnvelopeCommand(body), ct), HttpContext.TraceIdentifier);

    // ── IDM Sync Log (RLS-scoped; one screen for every role) ──────────────────────────────────────────
    [HttpGet("sync-log")]
    [Authorize(Policy = Perm.IntegrationIdmSyncView)]
    [EndpointSummary("IDM document sync log")]
    [EndpointDescription("Paged, RLS-scoped IDM document-outbox history (status/operation/type/filename/date/supplierId filters). Requires Integration.IdmSync.View — each role sees only its own rows.")]
    public async Task<Result<PagedResult<IdmSyncLogDto>>> SyncLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null, [FromQuery] string? operation = null, [FromQuery] string? idmEntityType = null,
        [FromQuery] string? fileName = null, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? supplierId = null, CancellationToken ct = default)
        => Result<PagedResult<IdmSyncLogDto>>.Ok(
            await _mediator.Send(new GetIdmSyncLogQuery(page, pageSize, status, operation, idmEntityType, fileName, fromDate, toDate, supplierId), ct),
            HttpContext.TraceIdentifier);

    [HttpGet("sync-log/{id:guid}/detail")]
    [Authorize(Policy = Perm.IntegrationIdmSyncView)]
    [EndpointSummary("IDM sync-log row detail")]
    [EndpointDescription("The elided request snapshot + raw IDM (XML) response for one row, fetched on demand. Requires Integration.IdmSync.View.")]
    public async Task<Result<IdmSyncLogDetailDto?>> SyncLogDetail(Guid id, CancellationToken ct)
        => Result<IdmSyncLogDetailDto?>.Ok(await _mediator.Send(new GetIdmSyncLogDetailQuery(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("sync-log/{id:guid}/retry")]
    [Authorize(Policy = Perm.IntegrationIdmSyncManage)]
    [EndpointSummary("Retry a failed IDM row")]
    [EndpointDescription("Re-arms a Failed/Unresolvable row to Pending (clears backoff + attempt count). Idempotency prevents an IDM duplicate. Requires Integration.IdmSync.Manage.")]
    public async Task<Result<bool>> Retry(Guid id, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new RetryIdmOutboxRowCommand(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("sync-log/{id:guid}/repush")]
    [Authorize(Policy = Perm.IntegrationIdmSyncManage)]
    [EndpointSummary("Re-push a synced IDM document")]
    [EndpointDescription("Queues a NEW Update push for a Success row's document (carrying its pid; the Create row stays terminal). Requires Integration.IdmSync.Manage.")]
    public async Task<Result<bool>> Repush(Guid id, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new RepushIdmDocumentCommand(id), ct), HttpContext.TraceIdentifier);
}
