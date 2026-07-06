using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.Ln.Commands;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R9 (TSD R9 §2.1) — config-driven LN outbound posting admin: per-transaction-type endpoint config CRUD,
/// save-time JSONata/contract validation, sample pin (D-R9-18), manual dry-post attestation + path
/// confirmation (D-R9-17/21), and the tri-state dispatch-mode switch (D-R9-2/11). Reads are Settings.Read;
/// every mutation is the high-blast-radius Integration.Admin.
/// </summary>
[ApiController]
[Authorize]
[Route("api/integration/ln-outbound")]
public class LnOutboundConfigController : ControllerBase
{
    private readonly IMediator _mediator;
    public LnOutboundConfigController(IMediator mediator) => _mediator = mediator;

    [HttpGet("configs")]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("List LN outbound endpoint configs")]
    [EndpointDescription("The tenant's per-transaction-type LN endpoint configs with drift flags, sample state (stale badge on builder-version drift) and attestation evidence. Requires Settings.Read.")]
    public async Task<Result<IReadOnlyList<LnEndpointConfigDto>>> GetConfigs(CancellationToken ct)
        => Result<IReadOnlyList<LnEndpointConfigDto>>.Ok(await _mediator.Send(new GetLnEndpointConfigsQuery(), ct), HttpContext.TraceIdentifier);

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
    public async Task<Result<Guid>> SaveConfig([FromBody] SaveLnEndpointConfigRequest body, CancellationToken ct)
        => Result<Guid>.Ok(await _mediator.Send(new SaveLnEndpointConfigCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPost("configs/validate")]
    [Authorize(Policy = Perm.IntegrationAdmin)]
    [EndpointSummary("Dry-validate a config shape")]
    [EndpointDescription("Runs the save-time pipeline without writing: per-slot errors + the rendered request preview against the supplied sample. (The Phase D mapping editor's live-eval endpoint.) Requires Integration.Admin.")]
    public async Task<Result<LnConfigValidationResultDto>> Validate([FromBody] ValidateLnEndpointConfigRequest body, CancellationToken ct)
        => Result<LnConfigValidationResultDto>.Ok(await _mediator.Send(new ValidateLnEndpointConfigCommand(body), ct), HttpContext.TraceIdentifier);

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
    public async Task<Result<bool>> SetDispatchMode(Guid id, [FromBody] SetLnDispatchModeRequest body, CancellationToken ct)
        => Result<bool>.Ok(await _mediator.Send(new SetLnDispatchModeCommand(id, body.Mode), ct), HttpContext.TraceIdentifier);

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
        => Result<bool>.Ok(await _mediator.Send(new DeleteLnEndpointConfigCommand(id), ct), HttpContext.TraceIdentifier);
}
