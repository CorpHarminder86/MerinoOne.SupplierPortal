using System.Text.Json;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm.Commands;

// ── Attachment-type config (Settings › Infor IDM) ─────────────────────────────────────────────────────────────

public record GetIdmAttachmentTypeConfigsQuery : IRequest<IReadOnlyList<IdmAttachmentTypeConfigDto>>;

public class GetIdmAttachmentTypeConfigsQueryHandler : IRequestHandler<GetIdmAttachmentTypeConfigsQuery, IReadOnlyList<IdmAttachmentTypeConfigDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IIdmExpressionCatalog _catalog;
    private readonly ISnapshotProviderRegistry _providers;
    public GetIdmAttachmentTypeConfigsQueryHandler(IAppDbContext db, ICurrentUser user, IIdmExpressionCatalog catalog, ISnapshotProviderRegistry providers)
    { _db = db; _user = user; _catalog = catalog; _providers = providers; }

    public async Task<IReadOnlyList<IdmAttachmentTypeConfigDto>> Handle(GetIdmAttachmentTypeConfigsQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var rows = await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tid && !c.IsDeleted)
            .OrderBy(c => c.OwnerEntityType).ThenBy(c => c.AttachmentType)
            .ToListAsync(ct);

        return rows.Select(c =>
        {
            var repo = _catalog.TryGet(c.IdmEntityType);
            var drifted = repo is not null && _catalog.Hash(c.CreateMappingExpression) != repo.CreateHash;
            return new IdmAttachmentTypeConfigDto(
                c.Id, c.AttachmentType, c.IdmEntityType,
                // Portal entity: the stored column wins; fall back to the provider for pre-2026-07-06 rows.
                string.IsNullOrEmpty(c.OwnerEntityType) ? _providers.TryGet(c.IdmEntityType)?.OwnerEntityType : c.OwnerEntityType,
                // R9 (§2.11) — defensive: a stored legacy dot-path array (unmigrated row) renders as its converted expression.
                IdmGateConversion.ConvertStoredValue(c.EligibilityGateExpr),
                c.CreateMappingExpression, c.MutateMappingExpression, c.IsEnabled, drifted, repo is not null);
        }).ToList();
    }
}

/// <summary>The (portal entity → IDM entity type) pairs a NEW mapping can target — one per registered snapshot
/// provider (anything else would enqueue rows the dispatcher can never send: "No snapshot provider for '...'").</summary>
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

/// <summary>
/// 2026-07-05 — deletes a mapping row (soft-delete) and UN-classifies its not-yet-pushed documents: clears
/// <c>DocumentUpload.idmEntityType</c> where it was stamped by this mapping and no IDM pid exists. Pushed documents
/// (pid present) KEEP the stamp — the document lives in IDM and the classification is still needed to resolve a
/// later Delete push. Existing outbox rows are untouched; the worker's conservative sweep marks any non-terminal
/// ones Unresolvable (config missing), from where Retry can resurrect them after re-creating the mapping.
/// </summary>
public record DeleteIdmAttachmentTypeConfigCommand(Guid Id) : IRequest<IdmConfigDeleteResultDto>;

public class DeleteIdmAttachmentTypeConfigCommandHandler : IRequestHandler<DeleteIdmAttachmentTypeConfigCommand, IdmConfigDeleteResultDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeleteIdmAttachmentTypeConfigCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<IdmConfigDeleteResultDto> Handle(DeleteIdmAttachmentTypeConfigCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return new IdmConfigDeleteResultDto(false, 0);

        var row = await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.Id && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return new IdmConfigDeleteResultDto(false, 0);

        var now = DateTime.UtcNow;

        // Un-classify UNPUSHED documents stamped by this mapping (same key the stamping used: idmEntityType +,
        // when the mapping targeted a specific attachment type, documentType — a catch-all NULL attachmentType
        // un-classifies every not-yet-pushed doc of that idmEntityType). Pid-bearing docs keep the stamp. The
        // attachment filter is built in C# (an inline `== null` OR on a null parameter is translated
        // inconsistently by EF, which would silently skip the un-classify).
        var attachmentType = row.AttachmentType;
        var clearQuery = _db.DocumentUploads.IgnoreQueryFilters()
            .Where(d => !d.IsDeleted && d.TenantId == tid && d.Pid == null && d.IdmEntityType == row.IdmEntityType);
        if (attachmentType != null) clearQuery = clearQuery.Where(d => d.DocumentType == attachmentType);
        var cleared = await clearQuery
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.IdmEntityType, (string?)null)
                .SetProperty(d => d.UpdatedBy, _user.UserCode)
                .SetProperty(d => d.UpdatedOn, now), ct);

        row.IsDeleted = true;
        row.DeletedOn = now;
        row.DeletedBy = _user.UserCode;
        await _db.SaveChangesAsync(ct);

        return new IdmConfigDeleteResultDto(true, cleared);
    }
}

public record SaveIdmAttachmentTypeConfigCommand(SaveIdmAttachmentTypeConfigRequest Body) : IRequest<Guid>;

public class SaveIdmAttachmentTypeConfigValidator : AbstractValidator<SaveIdmAttachmentTypeConfigCommand>
{
    // Portal entities that can own IDM-syncable documents (doc.DocumentUpload.OwnerEntityType values).
    private static readonly string[] PortalEntities = { "Asn", "Invoice", "Supplier" };

    public SaveIdmAttachmentTypeConfigValidator()
    {
        RuleFor(x => x.Body.OwnerEntityType).NotEmpty()
            .Must(v => PortalEntities.Contains(v)).WithMessage("Portal entity must be Asn, Invoice or Supplier.");
        // AttachmentType is OPTIONAL (null/blank = catch-all: every document of the portal entity).
        RuleFor(x => x.Body.AttachmentType).MaximumLength(50);
        RuleFor(x => x.Body.IdmEntityType).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Body.CreateMappingExpression).NotEmpty();
        // R9 (§2.11) — the gate is a JSONata boolean expression; blank = never satisfied (fail closed).
        RuleFor(x => x.Body.EligibilityGateExpr).NotNull();
    }
}

public class SaveIdmAttachmentTypeConfigCommandHandler : IRequestHandler<SaveIdmAttachmentTypeConfigCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IJsonataValidator _jsonata;
    public SaveIdmAttachmentTypeConfigCommandHandler(IAppDbContext db, ICurrentUser user, IJsonataValidator jsonata)
    { _db = db; _user = user; _jsonata = jsonata; }

    public async Task<Guid> Handle(SaveIdmAttachmentTypeConfigCommand request, CancellationToken ct)
    {
        var b = request.Body;
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");

        var attachmentType = string.IsNullOrWhiteSpace(b.AttachmentType) ? null : b.AttachmentType.Trim();

        // When a specific attachment type is given it must exist + be active in the tenant's catalogue. A null
        // (catch-all) mapping skips this check — it applies to every document of the portal entity.
        if (attachmentType is not null)
        {
            var typeOk = await _db.AttachmentTypes.IgnoreQueryFilters()
                .AnyAsync(t => t.TenantId == tid && t.Code == attachmentType && t.IsActive && !t.IsDeleted, ct);
            if (!typeOk) throw new ValidationException($"Attachment type '{attachmentType}' is not an active type in this tenant.");
        }

        var createErr = _jsonata.Validate(b.CreateMappingExpression);
        if (createErr is not null) throw new ValidationException($"Create mapping expression does not compile: {createErr}");
        if (!string.IsNullOrWhiteSpace(b.MutateMappingExpression))
        {
            var mutateErr = _jsonata.Validate(b.MutateMappingExpression!);
            if (mutateErr is not null) throw new ValidationException($"Mutate mapping expression does not compile: {mutateErr}");
        }

        // R9 (§2.11) — the gate is a JSONata expression now; compile it at save like the mappings. A legacy
        // dot-path array pasted in is converted transparently (the same conversion migration 0049 applied).
        var gateExpr = IdmGateConversion.ConvertStoredValue(b.EligibilityGateExpr?.Trim() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(gateExpr))
        {
            var gateErr = _jsonata.Validate(gateExpr);
            if (gateErr is not null) throw new ValidationException($"Eligibility gate expression does not compile: {gateErr}");
        }
        var now = DateTime.UtcNow;

        var existing = b.Id is { } id
            ? await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid, ct)
            // Upsert key = (tenant, ownerEntityType, attachmentType). EF renders the null attachmentType as IS NULL.
            : await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(
                c => c.TenantId == tid && c.OwnerEntityType == b.OwnerEntityType && c.AttachmentType == attachmentType && !c.IsDeleted, ct);

        if (existing is null)
        {
            existing = new IdmAttachmentTypeConfig { TenantId = tid, CreatedBy = _user.UserCode };
            _db.IdmAttachmentTypeConfigs.Add(existing);
        }
        else
        {
            existing.UpdatedBy = _user.UserCode;
            existing.UpdatedOn = now;
        }

        existing.OwnerEntityType = b.OwnerEntityType;
        existing.AttachmentType = attachmentType;
        existing.IdmEntityType = b.IdmEntityType;
        existing.EligibilityGateExpr = gateExpr;
        existing.CreateMappingExpression = b.CreateMappingExpression;
        existing.MutateMappingExpression = string.IsNullOrWhiteSpace(b.MutateMappingExpression) ? null : b.MutateMappingExpression;
        existing.IsEnabled = b.IsEnabled;

        await _db.SaveChangesAsync(ct);
        return existing.Id;
    }
}

/// <summary>D6 — restore a config row's expressions from the repo default and re-stamp the seed hash.</summary>
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
        var row = await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == request.Id && c.TenantId == tid, ct);
        if (row is null) return false;
        var repo = _catalog.TryGet(row.IdmEntityType);
        if (repo is null) return false;

        row.CreateMappingExpression = repo.CreateExpression;
        row.CreateMappingSeedHash = repo.CreateHash;
        row.MutateMappingExpression = repo.MutateExpression;
        row.MutateMappingSeedHash = repo.MutateHash;
        row.UpdatedBy = _user.UserCode;
        row.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

// ── Outbound endpoint config (Integration › Endpoints) ────────────────────────────────────────────────────────

public record GetOutboundEndpointConfigsQuery : IRequest<IReadOnlyList<OutboundEndpointConfigDto>>;

public class GetOutboundEndpointConfigsQueryHandler : IRequestHandler<GetOutboundEndpointConfigsQuery, IReadOnlyList<OutboundEndpointConfigDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetOutboundEndpointConfigsQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<IReadOnlyList<OutboundEndpointConfigDto>> Handle(GetOutboundEndpointConfigsQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        return await _db.OutboundEndpointConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.TenantId == tid && !e.IsDeleted)
            .OrderBy(e => e.EndpointKey)
            .Select(e => new OutboundEndpointConfigDto(e.Id, e.EndpointKey, e.HttpMethod, e.RelativePath,
                e.StaticHeadersJson, e.AckParserKey, e.DefaultAcl, e.EntityName, e.IsEnabled))
            .ToListAsync(ct);
    }
}

public record SaveOutboundEndpointConfigCommand(SaveOutboundEndpointConfigRequest Body) : IRequest<Guid>;

public class SaveOutboundEndpointConfigValidator : AbstractValidator<SaveOutboundEndpointConfigCommand>
{
    private static readonly string[] Methods = { "POST", "PUT", "DELETE" };
    public SaveOutboundEndpointConfigValidator()
    {
        RuleFor(x => x.Body.EndpointKey).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Body.RelativePath).NotEmpty().MaximumLength(400);
        RuleFor(x => x.Body.HttpMethod).Must(m => Methods.Contains(m, StringComparer.OrdinalIgnoreCase))
            .WithMessage("HTTP method must be POST, PUT or DELETE.");
    }
}

public class SaveOutboundEndpointConfigCommandHandler : IRequestHandler<SaveOutboundEndpointConfigCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public SaveOutboundEndpointConfigCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Guid> Handle(SaveOutboundEndpointConfigCommand request, CancellationToken ct)
    {
        var b = request.Body;
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");
        if (!string.IsNullOrWhiteSpace(b.StaticHeadersJson))
        {
            try { using var _ = JsonDocument.Parse(b.StaticHeadersJson!); }
            catch (JsonException) { throw new ValidationException("Static headers must be a valid JSON object."); }
        }

        var now = DateTime.UtcNow;
        var existing = b.Id is { } id
            ? await _db.OutboundEndpointConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tid, ct)
            : await _db.OutboundEndpointConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.TenantId == tid && e.EndpointKey == b.EndpointKey && !e.IsDeleted, ct);

        if (existing is null)
        {
            existing = new OutboundEndpointConfig { TenantId = tid, TargetSystem = "IDM", CreatedBy = _user.UserCode };
            _db.OutboundEndpointConfigs.Add(existing);
        }
        else { existing.UpdatedBy = _user.UserCode; existing.UpdatedOn = now; }

        existing.EndpointKey = b.EndpointKey;
        existing.HttpMethod = b.HttpMethod.ToUpperInvariant();
        existing.RelativePath = b.RelativePath;
        existing.StaticHeadersJson = b.StaticHeadersJson;
        existing.AckParserKey = b.AckParserKey;
        existing.DefaultAcl = b.DefaultAcl;
        existing.EntityName = b.EntityName;
        existing.IsEnabled = b.IsEnabled;

        await _db.SaveChangesAsync(ct);
        return existing.Id;
    }
}

public record DeleteOutboundEndpointConfigCommand(Guid Id) : IRequest<bool>;

public class DeleteOutboundEndpointConfigCommandHandler : IRequestHandler<DeleteOutboundEndpointConfigCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeleteOutboundEndpointConfigCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<bool> Handle(DeleteOutboundEndpointConfigCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var row = await _db.OutboundEndpointConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == request.Id && e.TenantId == tid, ct);
        if (row is null) return false;
        row.IsDeleted = true; row.DeletedOn = DateTime.UtcNow; row.DeletedBy = _user.UserCode;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Validate: acquire a tenant OAuth token; warn when the ApiBaseUrl looks LN-suite-scoped (/LN) vs a tenant-root IDM path.</summary>
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
        var ep = await _db.OutboundEndpointConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.TenantId == tid, ct);
        if (ep is null) return new ValidateOutboundEndpointResultDto(false, false, false, 0, "Endpoint not found.");

        var conn = await _connections.GetCurrentAsync(ct);
        if (conn is null || !conn.IsConfigured)
            return new ValidateOutboundEndpointResultDto(false, false, false, 0, "Tenant Infor connection is not configured.");

        var token = await _tokens.GetAccessTokenAsync(ct);
        var tokenOk = !string.IsNullOrEmpty(token);

        // An absolute relativePath (http/https) is used verbatim by the Live client — no base concatenation.
        var isAbsolute = ep.RelativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || ep.RelativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var target = isAbsolute ? ep.RelativePath : $"{conn.ApiBaseUrl.TrimEnd('/')}/{ep.RelativePath.TrimStart('/')}";
        var warnLn = !isAbsolute && conn.ApiBaseUrl.Contains("/LN", StringComparison.OrdinalIgnoreCase)
            ? " WARNING: the tenant ApiBaseUrl looks LN-suite-scoped (/LN); set an ABSOLUTE https://… relativePath for IDM (used verbatim) or use the tenant-root ION API base."
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

        // Resolve the config: by the stamped idmEntityType, else by matching the document's owner entity + type
        // (a specific-attachment-type config wins; a catch-all — null attachmentType — matches any type of the entity).
        IdmAttachmentTypeConfig? cfg;
        if (doc.IdmEntityType != null)
        {
            cfg = await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tid && c.IdmEntityType == doc.IdmEntityType && !c.IsDeleted, ct);
        }
        else
        {
            var candidates = await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.TenantId == tid && !c.IsDeleted && c.OwnerEntityType == doc.OwnerEntityType
                            && (c.AttachmentType == null || c.AttachmentType == doc.DocumentType))
                .ToListAsync(ct);
            cfg = candidates.OrderBy(c => c.AttachmentType == null ? 1 : 0).FirstOrDefault();   // specific type first
        }
        if (cfg is null)
            return new IdmTestBenchResultDto(false, new[] { "No IDM config maps this document's type." }, "{}", "{}", null, null);

        var provider = _providers.TryGet(cfg.IdmEntityType);
        if (provider is null)
            return new IdmTestBenchResultDto(false, new[] { $"No snapshot provider for '{cfg.IdmEntityType}'." }, "{}", "{}", null, null);

        // Display snapshot (no file content) → clean envelope; gate evaluated against it.
        var displaySnapshot = await provider.BuildSnapshotAsync(tid, doc.OwnerEntityId, doc.Id, includeFileContent: false, ct);
        if (displaySnapshot is null)
            return new IdmTestBenchResultDto(false, new[] { "Snapshot could not be assembled." }, "{}", "{}", null, null);

        var gateSatisfied = _gate.IsSatisfied(cfg.EligibilityGateExpr, displaySnapshot);
        var failures = gateSatisfied
            ? Array.Empty<string>()
            // R9 (§2.11) — a single JSONata expression has no per-path breakdown; surface the expression itself.
            : new[] { $"Eligibility gate returned false: {cfg.EligibilityGateExpr}" };

        var displayEnvelope = await _builder.BuildAsync(cfg.CreateMappingExpression, displaySnapshot, ct);
        var headersJson = JsonSerializer.Serialize(displayEnvelope.Headers);

        int? dryStatus = null; string? dryResponse = null;
        if (request.Body.DryRun)
        {
            var realSnapshot = await provider.BuildSnapshotAsync(tid, doc.OwnerEntityId, doc.Id, includeFileContent: true, ct);
            if (realSnapshot is not null)
            {
                var realEnvelope = await _builder.BuildAsync(cfg.CreateMappingExpression, realSnapshot, ct);
                var result = await _client.SendAsync(tid, "IDM.Item.Create", realEnvelope, ct);
                dryStatus = result.StatusCode;
                dryResponse = result.Body;
            }
        }

        return new IdmTestBenchResultDto(gateSatisfied, failures, headersJson, displayEnvelope.Body, dryStatus, dryResponse);
    }
}
