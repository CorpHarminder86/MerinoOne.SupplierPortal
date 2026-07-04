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
    public GetIdmAttachmentTypeConfigsQueryHandler(IAppDbContext db, ICurrentUser user, IIdmExpressionCatalog catalog)
    { _db = db; _user = user; _catalog = catalog; }

    public async Task<IReadOnlyList<IdmAttachmentTypeConfigDto>> Handle(GetIdmAttachmentTypeConfigsQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var rows = await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tid && !c.IsDeleted)
            .OrderBy(c => c.AttachmentType)
            .ToListAsync(ct);

        return rows.Select(c =>
        {
            var repo = _catalog.TryGet(c.IdmEntityType);
            var drifted = repo is not null && _catalog.Hash(c.CreateMappingExpression) != repo.CreateHash;
            return new IdmAttachmentTypeConfigDto(
                c.Id, c.AttachmentType, c.IdmEntityType, ParsePaths(c.EligibilityGateJson),
                c.CreateMappingExpression, c.MutateMappingExpression, c.IsEnabled, drifted, repo is not null);
        }).ToList();
    }

    private static IReadOnlyList<string> ParsePaths(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new List<string>(); }
    }
}

public record SaveIdmAttachmentTypeConfigCommand(SaveIdmAttachmentTypeConfigRequest Body) : IRequest<Guid>;

public class SaveIdmAttachmentTypeConfigValidator : AbstractValidator<SaveIdmAttachmentTypeConfigCommand>
{
    public SaveIdmAttachmentTypeConfigValidator()
    {
        RuleFor(x => x.Body.AttachmentType).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.IdmEntityType).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Body.CreateMappingExpression).NotEmpty();
        RuleFor(x => x.Body.EligibilityGatePaths).NotNull();
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

        // The attachment type must exist + be active in the tenant's catalogue.
        var typeOk = await _db.AttachmentTypes.IgnoreQueryFilters()
            .AnyAsync(t => t.TenantId == tid && t.Code == b.AttachmentType && t.IsActive && !t.IsDeleted, ct);
        if (!typeOk) throw new ValidationException($"Attachment type '{b.AttachmentType}' is not an active type in this tenant.");

        var createErr = _jsonata.Validate(b.CreateMappingExpression);
        if (createErr is not null) throw new ValidationException($"Create mapping expression does not compile: {createErr}");
        if (!string.IsNullOrWhiteSpace(b.MutateMappingExpression))
        {
            var mutateErr = _jsonata.Validate(b.MutateMappingExpression!);
            if (mutateErr is not null) throw new ValidationException($"Mutate mapping expression does not compile: {mutateErr}");
        }

        var gateJson = JsonSerializer.Serialize(b.EligibilityGatePaths);
        var now = DateTime.UtcNow;

        var existing = b.Id is { } id
            ? await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid, ct)
            : await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.TenantId == tid && c.AttachmentType == b.AttachmentType && !c.IsDeleted, ct);

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

        existing.AttachmentType = b.AttachmentType;
        existing.IdmEntityType = b.IdmEntityType;
        existing.EligibilityGateJson = gateJson;
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
                e.StaticHeadersJson, e.AckParserKey, e.DefaultAcl, e.IsEnabled))
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
        var warnLn = conn.ApiBaseUrl.Contains("/LN", StringComparison.OrdinalIgnoreCase)
            ? " WARNING: the tenant ApiBaseUrl looks LN-suite-scoped (/LN); the IDM relativePath may need the tenant-root ION API base."
            : string.Empty;

        var msg = tokenOk
            ? $"OAuth token acquired. Target: {conn.ApiBaseUrl.TrimEnd('/')}/{ep.RelativePath.TrimStart('/')}.{warnLn}"
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

        // Resolve the config: by the stamped idmEntityType, else by the document's type.
        var cfg = doc.IdmEntityType != null
            ? await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tid && c.IdmEntityType == doc.IdmEntityType && !c.IsDeleted, ct)
            : await _db.IdmAttachmentTypeConfigs.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tid && c.AttachmentType == doc.DocumentType && !c.IsDeleted, ct);
        if (cfg is null)
            return new IdmTestBenchResultDto(false, new[] { "No IDM config maps this document's type." }, "{}", "{}", null, null);

        var provider = _providers.TryGet(cfg.IdmEntityType);
        if (provider is null)
            return new IdmTestBenchResultDto(false, new[] { $"No snapshot provider for '{cfg.IdmEntityType}'." }, "{}", "{}", null, null);

        // Display snapshot (no file content) → clean envelope; gate evaluated against it.
        var displaySnapshot = await provider.BuildSnapshotAsync(tid, doc.OwnerEntityId, doc.Id, includeFileContent: false, ct);
        if (displaySnapshot is null)
            return new IdmTestBenchResultDto(false, new[] { "Snapshot could not be assembled." }, "{}", "{}", null, null);

        var gateSatisfied = _gate.IsSatisfied(cfg.EligibilityGateJson, displaySnapshot);
        var failures = gateSatisfied ? Array.Empty<string>() : GateFailures(cfg.EligibilityGateJson, displaySnapshot);

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

    private static string[] GateFailures(string gateJson, object snapshot)
    {
        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(gateJson) ?? Array.Empty<string>();
            return paths;
        }
        catch (JsonException) { return new[] { "Malformed eligibility gate JSON." }; }
    }
}
