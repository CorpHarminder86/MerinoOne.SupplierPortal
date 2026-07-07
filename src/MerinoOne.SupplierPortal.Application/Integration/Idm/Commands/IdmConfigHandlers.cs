using System.Text.Json;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm.Commands;

// R10 — the R8 config CRUD (IdmAttachmentTypeConfig + OutboundEndpointConfig) is GONE: Document-kind rows on
// integration.OutboundIntegrationConfig carry mapping + routing in one place and are managed by the unified
// Save/Delete/mode handlers (Application/Integration/Ln/Commands). What stays here is the Document-kind
// tooling: entity-type options, repo-default restore, connection validate, and the test bench.

/// <summary>The (portal entity → IDM entity type) pairs a NEW Document mapping can target — one per registered
/// snapshot provider (anything else would enqueue rows the dispatcher can never send).</summary>
public record GetIdmEntityTypeOptionsQuery : IRequest<IReadOnlyList<IdmEntityTypeOptionDto>>;

public class GetIdmEntityTypeOptionsQueryHandler : IRequestHandler<GetIdmEntityTypeOptionsQuery, IReadOnlyList<IdmEntityTypeOptionDto>>
{
    private readonly ISnapshotProviderRegistry _providers;
    public GetIdmEntityTypeOptionsQueryHandler(ISnapshotProviderRegistry providers) => _providers = providers;

    public Task<IReadOnlyList<IdmEntityTypeOptionDto>> Handle(GetIdmEntityTypeOptionsQuery request, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IdmEntityTypeOptionDto>>(_providers.All
            .Select(p => new IdmEntityTypeOptionDto(p.IdmEntityType, p.OwnerEntityType))
            .OrderBy(t => t.OwnerEntityType).ThenBy(t => t.IdmEntityType).ToList());
}

/// <summary>D6 — restore a Document-kind config row's mapping expressions from the repo default (keyed by
/// <c>targetEntityName</c>) and re-stamp the seed hashes.</summary>
public record RestoreIdmDefaultExpressionCommand(Guid Id) : IRequest<bool>;

public class RestoreIdmDefaultExpressionCommandHandler : IRequestHandler<RestoreIdmDefaultExpressionCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IIdmExpressionCatalog _catalog;
    public RestoreIdmDefaultExpressionCommandHandler(IAppDbContext db, ICurrentUser user, IIdmExpressionCatalog catalog)
    { _db = db; _user = user; _catalog = catalog; }

    public async Task<bool> Handle(RestoreIdmDefaultExpressionCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var row = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.Id && c.TenantId == tid
                                      && c.Kind == OutboundIntegrationKind.Document && !c.IsDeleted, ct);
        if (row?.TargetEntityName is null) return false;
        var repo = _catalog.TryGet(row.TargetEntityName);
        if (repo is null) return false;

        row.RequestMappingExpr = repo.CreateExpression;
        row.RequestMappingSeedHash = repo.CreateHash;
        row.MutateMappingExpr = repo.MutateExpression;
        row.MutateMappingSeedHash = repo.MutateHash;
        row.GateVersion++;
        row.UpdatedBy = _user.UserCode;
        row.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Validate: acquire a tenant OAuth token; warn when the ApiBaseUrl looks LN-suite-scoped (/LN) vs a
/// tenant-root IDM path. R10: reads the unified config row's create path.</summary>
public record ValidateOutboundEndpointCommand(Guid Id) : IRequest<ValidateOutboundEndpointResultDto>;

public class ValidateOutboundEndpointCommandHandler : IRequestHandler<ValidateOutboundEndpointCommand, ValidateOutboundEndpointResultDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IInforConnectionProvider _connections;
    private readonly IInforTokenProvider _tokens;
    public ValidateOutboundEndpointCommandHandler(IAppDbContext db, ICurrentUser user, IInforConnectionProvider connections, IInforTokenProvider tokens)
    { _db = db; _user = user; _connections = connections; _tokens = tokens; }

    public async Task<ValidateOutboundEndpointResultDto> Handle(ValidateOutboundEndpointCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var ep = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.TenantId == tid && !e.IsDeleted, ct);
        if (ep is null) return new ValidateOutboundEndpointResultDto(false, false, false, 0, "Integration config not found.");

        var conn = await _connections.GetCurrentAsync(ct);
        if (conn is null || !conn.IsConfigured)
            return new ValidateOutboundEndpointResultDto(false, false, false, 0, "Tenant Infor connection is not configured.");

        var token = await _tokens.GetAccessTokenAsync(ct);
        var tokenOk = !string.IsNullOrEmpty(token);

        // An absolute path (http/https) is used verbatim by the Live client — no base concatenation.
        var isAbsolute = ep.EndpointPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || ep.EndpointPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var target = isAbsolute ? ep.EndpointPath : $"{conn.ApiBaseUrl.TrimEnd('/')}/{ep.EndpointPath.TrimStart('/')}";
        var warnLn = ep.Kind == OutboundIntegrationKind.Document && !isAbsolute
                     && conn.ApiBaseUrl.Contains("/LN", StringComparison.OrdinalIgnoreCase)
            ? " WARNING: the tenant ApiBaseUrl looks LN-suite-scoped (/LN); set an ABSOLUTE https://… path for IDM (used verbatim) or use the tenant-root ION API base."
            : string.Empty;

        var msg = tokenOk
            ? $"OAuth token acquired. Target: {target}.{warnLn}"
            : "Could not acquire an OAuth token for the tenant.";
        return new ValidateOutboundEndpointResultDto(tokenOk, tokenOk, false, 0, msg);
    }
}

// ── Test bench ────────────────────────────────────────────────────────────────────────────────────────────────

public record SearchIdmTestDocumentsQuery(string? Search) : IRequest<IReadOnlyList<IdmDocumentPickDto>>;

public class SearchIdmTestDocumentsQueryHandler : IRequestHandler<SearchIdmTestDocumentsQuery, IReadOnlyList<IdmDocumentPickDto>>
{
    private readonly IAppDbContext _db;
    public SearchIdmTestDocumentsQueryHandler(IAppDbContext db) { _db = db; }

    public async Task<IReadOnlyList<IdmDocumentPickDto>> Handle(SearchIdmTestDocumentsQuery request, CancellationToken ct)
    {
        // RLS-scoped (DocumentUpload : BaseAggregateRoot) — the bench only sees documents the caller may access.
        var q = _db.DocumentUploads.AsNoTracking().Where(d => !d.IsDeleted);
        if (!string.IsNullOrWhiteSpace(request.Search))
            q = q.Where(d => d.FileName.Contains(request.Search) || d.DocumentType.Contains(request.Search));
        return await q.OrderByDescending(d => d.Seq).Take(25)
            .Select(d => new IdmDocumentPickDto(d.Id, d.FileName, d.DocumentType, d.OwnerEntityType, d.IdmEntityType, d.Pid != null))
            .ToListAsync(ct);
    }
}

/// <summary>
/// Test bench: assemble the snapshot for a chosen document, render the exact Create envelope (base64 elided), report
/// gate satisfaction, and optionally dry-run POST the real payload and return the parsed ack.
/// </summary>
public record TestIdmEnvelopeCommand(IdmTestBenchRequest Body) : IRequest<IdmTestBenchResultDto>;

public class TestIdmEnvelopeCommandHandler : IRequestHandler<TestIdmEnvelopeCommand, IdmTestBenchResultDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISnapshotProviderRegistry _providers;
    private readonly IEligibilityGate _gate;
    private readonly IOutboundRequestBuilder _builder;
    private readonly IIdmClient _client;
    public TestIdmEnvelopeCommandHandler(IAppDbContext db, ICurrentUser user, ISnapshotProviderRegistry providers,
        IEligibilityGate gate, IOutboundRequestBuilder builder, IIdmClient client)
    { _db = db; _user = user; _providers = providers; _gate = gate; _builder = builder; _client = client; }

    public async Task<IdmTestBenchResultDto> Handle(TestIdmEnvelopeCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid)
            return new IdmTestBenchResultDto(false, new[] { "No tenant context." }, "{}", "{}", null, null);

        var doc = await _db.DocumentUploads.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.Body.DocumentUploadId, ct);
        if (doc is null)
            return new IdmTestBenchResultDto(false, new[] { "Document not found / not accessible." }, "{}", "{}", null, null);

        // Resolve the Document-kind config: by the stamped idmEntityType, else by matching the document's owner
        // entity + type (a specific-attachment-type config wins over the catch-all).
        OutboundIntegrationConfig? cfg;
        if (doc.IdmEntityType != null)
        {
            cfg = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tid && c.Kind == OutboundIntegrationKind.Document
                                          && c.TargetEntityName == doc.IdmEntityType && !c.IsDeleted, ct);
        }
        else
        {
            var candidates = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tid && c.Kind == OutboundIntegrationKind.Document && !c.IsDeleted
                            && c.PortalEntity == doc.OwnerEntityType
                            && (c.AttachmentType == null || c.AttachmentType == doc.DocumentType))
                .ToListAsync(ct);
            cfg = candidates.OrderBy(c => c.AttachmentType == null ? 1 : 0).FirstOrDefault();   // specific type first
        }
        if (cfg?.TargetEntityName is null)
            return new IdmTestBenchResultDto(false, new[] { "No Document integration maps this document's type." }, "{}", "{}", null, null);

        var provider = _providers.TryGet(cfg.TargetEntityName);
        if (provider is null)
            return new IdmTestBenchResultDto(false, new[] { $"No snapshot provider for '{cfg.TargetEntityName}'." }, "{}", "{}", null, null);

        // Display snapshot (no file content) → clean envelope; gate evaluated against it.
        var displaySnapshot = await provider.BuildSnapshotAsync(tid, doc.OwnerEntityId, doc.Id, includeFileContent: false, ct);
        if (displaySnapshot is null)
            return new IdmTestBenchResultDto(false, new[] { "Snapshot could not be assembled." }, "{}", "{}", null, null);

        // R10 gate rule (aligned with the LN plane): blank gate = no gate = eligible.
        var gateSatisfied = string.IsNullOrWhiteSpace(cfg.EligibilityGateExpr)
                            || _gate.IsSatisfied(cfg.EligibilityGateExpr, displaySnapshot);
        var failures = gateSatisfied
            ? Array.Empty<string>()
            : new[] { $"Eligibility gate returned false: {cfg.EligibilityGateExpr}" };

        var displayEnvelope = await _builder.BuildAsync(cfg.RequestMappingExpr, displaySnapshot, ct);
        var headersJson = JsonSerializer.Serialize(displayEnvelope.Headers);

        int? dryStatus = null; string? dryResponse = null;
        if (request.Body.DryRun)
        {
            var realSnapshot = await provider.BuildSnapshotAsync(tid, doc.OwnerEntityId, doc.Id, includeFileContent: true, ct);
            if (realSnapshot is not null)
            {
                var realEnvelope = await _builder.BuildAsync(cfg.RequestMappingExpr, realSnapshot, ct);
                var result = await _client.SendAsync(tid, IdmOutboxOperation.Create, cfg.HttpVerb, cfg.EndpointPath, realEnvelope, ct);
                dryStatus = result.StatusCode;
                dryResponse = result.Body;
            }
        }

        return new IdmTestBenchResultDto(gateSatisfied, failures, headersJson, displayEnvelope.Body, dryStatus, dryResponse);
    }
}
