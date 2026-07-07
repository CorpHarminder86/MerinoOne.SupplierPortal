using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.CandidateFilters;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Ln.Commands;

// ── R9 (TSD R9 §2.1) — LN endpoint-config admin: CRUD + validate + pin sample + attest + dispatch-mode ──────────

public record GetOutboundIntegrationConfigsQuery : IRequest<IReadOnlyList<OutboundIntegrationConfigDto>>;

public class GetOutboundIntegrationConfigsQueryHandler : IRequestHandler<GetOutboundIntegrationConfigsQuery, IReadOnlyList<OutboundIntegrationConfigDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnExpressionCatalog _catalog;
    public GetOutboundIntegrationConfigsQueryHandler(IAppDbContext db, ICurrentUser user, ILnExpressionCatalog catalog)
    { _db = db; _user = user; _catalog = catalog; }

    public async Task<IReadOnlyList<OutboundIntegrationConfigDto>> Handle(GetOutboundIntegrationConfigsQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var rows = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
            .Include(c => c.ConnectionPoint)
            .Where(c => c.TenantId == tid && !c.IsDeleted)
            .OrderBy(c => c.Kind).ThenBy(c => c.TransactionType).ThenBy(c => c.PortalEntity).ThenBy(c => c.AttachmentType)
            .ToListAsync(ct);

        return rows.Select(c =>
        {
            // Repo-default drift + samples are Transaction-kind concepts (the expression catalog is keyed
            // by transaction type; Document rows have no compiled input-document builder).
            var repo = c.Kind == OutboundIntegrationKind.Transaction && c.TransactionType is not null
                ? _catalog.TryGet(c.TransactionType) : null;
            return new OutboundIntegrationConfigDto(
                c.Id, c.Seq, c.Kind.ToString(), c.ConnectionPointId, c.ConnectionPoint?.Name,
                c.TransactionType, c.PortalEntity, c.AttachmentType, c.TargetEntityName, c.ContextJson,
                c.EndpointPath, c.HttpVerb, c.MutatePath, c.MutateVerb, c.DeletePath, c.DeleteVerb,
                c.StaticHeadersJson, c.RequestFormat, c.ResponseFormat,
                c.DispatchMode.ToString(), c.EligibilityGateExpr, c.RequestMappingExpr, c.MutateMappingExpr,
                c.ResponseMappingExpr, c.AckMappingExpr, c.CandidateFilterName, c.CandidateFilterParams, c.GateVersion,
                HasSample: !string.IsNullOrWhiteSpace(c.SampleDocumentJson),
                c.SampleBuilderVersion,
                SampleStale: c.Kind == OutboundIntegrationKind.Transaction
                             && !string.IsNullOrWhiteSpace(c.SampleDocumentJson)
                             && c.SampleBuilderVersion != LnInputDocumentVersions.For(c.PortalEntity),
                c.SampleDocumentJson, c.ResponseSampleJson, c.AckSampleJson,
                RequestDrifted: repo is not null && _catalog.Hash(c.RequestMappingExpr) != repo.RequestHash,
                ResponseDrifted: repo is not null && c.ResponseMappingExpr is not null && _catalog.Hash(c.ResponseMappingExpr) != repo.ResponseHash,
                AckDrifted: repo is not null && c.AckMappingExpr is not null && _catalog.Hash(c.AckMappingExpr) != repo.AckHash,
                c.VerifiedBy, c.VerifiedAt, c.VerifiedNote, c.PathConfirmed,
                c.CreatedOn, c.UpdatedOn);
        }).ToList();
    }
}

public record GetLnCandidateFiltersQuery(string? PortalEntity) : IRequest<IReadOnlyList<LnCandidateFilterDto>>;

public class GetLnCandidateFiltersQueryHandler : IRequestHandler<GetLnCandidateFiltersQuery, IReadOnlyList<LnCandidateFilterDto>>
{
    private readonly ICandidateFilterRegistry _registry;
    public GetLnCandidateFiltersQueryHandler(ICandidateFilterRegistry registry) => _registry = registry;

    public Task<IReadOnlyList<LnCandidateFilterDto>> Handle(GetLnCandidateFiltersQuery request, CancellationToken ct)
    {
        var filters = string.IsNullOrWhiteSpace(request.PortalEntity) ? _registry.All : _registry.ForEntity(request.PortalEntity);
        return Task.FromResult<IReadOnlyList<LnCandidateFilterDto>>(filters
            .Select(f => new LnCandidateFilterDto(f.PortalEntity, f.Name, f.IsParameterized,
                f.IsParameterized ? "{\"statuses\":[\"...\"]}" : null))
            .OrderBy(f => f.PortalEntity).ThenBy(f => f.Name).ToList());
    }
}

/// <summary>RLS-scoped recent entities of the config's portalEntity for the sample-pin picker (D-R9-18).</summary>
public record SearchLnSampleCandidatesQuery(string PortalEntity, string? Search) : IRequest<IReadOnlyList<LnSampleCandidateDto>>;

public class SearchLnSampleCandidatesQueryHandler : IRequestHandler<SearchLnSampleCandidatesQuery, IReadOnlyList<LnSampleCandidateDto>>
{
    private const int Take = 20;
    private readonly IAppDbContext _db;
    public SearchLnSampleCandidatesQueryHandler(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<LnSampleCandidateDto>> Handle(SearchLnSampleCandidatesQuery request, CancellationToken ct)
    {
        var s = request.Search?.Trim();
        // Ambient RLS deliberately kept ON — the picker offers only rows the admin can see.
        switch (request.PortalEntity)
        {
            case LnPortalEntity.Invoice:
                return await _db.Invoices.AsNoTracking()
                    .Where(i => s == null || i.InvoiceNumber.Contains(s))
                    .OrderByDescending(i => i.CreatedOn).Take(Take)
                    .Select(i => new LnSampleCandidateDto(i.Id, i.InvoiceNumber, i.InvoiceStatus.ToString(), i.CreatedOn))
                    .ToListAsync(ct);
            case LnPortalEntity.Asn:
                return await _db.Asns.AsNoTracking()
                    .Where(a => s == null || a.AsnNumber.Contains(s))
                    .OrderByDescending(a => a.CreatedOn).Take(Take)
                    .Select(a => new LnSampleCandidateDto(a.Id, a.AsnNumber, a.AsnStatus.ToString(), a.CreatedOn))
                    .ToListAsync(ct);
            case LnPortalEntity.PurchaseOrder:
                return await _db.PurchaseOrders.AsNoTracking()
                    .Where(p => s == null || p.PoNumber.Contains(s))
                    .OrderByDescending(p => p.CreatedOn).Take(Take)
                    .Select(p => new LnSampleCandidateDto(p.Id, p.PoNumber, p.PoStatus.ToString(), p.CreatedOn))
                    .ToListAsync(ct);
            case LnPortalEntity.Supplier:
                return await _db.Suppliers.AsNoTracking()
                    .Where(x => s == null || x.SupplierCode.Contains(s) || x.LegalName.Contains(s))
                    .OrderByDescending(x => x.CreatedOn).Take(Take)
                    .Select(x => new LnSampleCandidateDto(x.Id, x.SupplierCode, x.RegistrationStatus.ToString(), x.CreatedOn))
                    .ToListAsync(ct);
            case LnPortalEntity.SupplierChange:
                return await _db.SupplierChangeRequests.AsNoTracking()
                    .Where(c => s == null || (c.Summary != null && c.Summary.Contains(s)))
                    .OrderByDescending(c => c.CreatedOn).Take(Take)
                    .Select(c => new LnSampleCandidateDto(c.Id, c.Summary ?? c.Id.ToString(), c.ChangeStatus.ToString(), c.CreatedOn))
                    .ToListAsync(ct);
            case LnPortalEntity.PoNegotiation:
                return await _db.PurchaseOrderNegotiations.AsNoTracking()
                    .Where(n => s == null || n.PoNumber.Contains(s))
                    .OrderByDescending(n => n.CreatedOn).Take(Take)
                    .Select(n => new LnSampleCandidateDto(n.Id, n.PoNumber, n.NegotiationStatus.ToString(), n.CreatedOn))
                    .ToListAsync(ct);
            default:
                return Array.Empty<LnSampleCandidateDto>();
        }
    }
}

// ── Shared save-time validation pipeline (used by Save, Validate and the → Dynamic gate) ────────────────────────

/// <summary>
/// The seven save-time checks (TSD §2.1/§2.3/§2.5a): known transaction type + buildable portalEntity;
/// all expressions compile; gate → strict boolean vs the pinned sample; request evaluates vs the sample;
/// response/ack → CLOSED contract vs their samples (unknown keys BLOCK); candidateFilterName → registry.
/// </summary>
public static class LnConfigValidationPipeline
{
    private static readonly HashSet<string> KnownTransactionTypes = new(StringComparer.Ordinal)
    {
        OutboxTransactionType.PoAcknowledge, OutboxTransactionType.PoAccept, OutboxTransactionType.PoReject,
        OutboxTransactionType.AsnPost, OutboxTransactionType.InvoicePost, OutboxTransactionType.SupplierSync,
        OutboxTransactionType.SupplierChange, OutboxTransactionType.PoNegotiationApprove,
    };

    public static LnConfigValidationResultDto Run(
        ILnMappingService mapping,
        ILnInputDocumentBuilderRegistry builders,
        ICandidateFilterRegistry filters,
        string? transactionType,
        string portalEntity,
        string? gateExpr, string? requestExpr, string? responseExpr, string? ackExpr,
        string? candidateFilterName, string? candidateFilterParams,
        string? sampleDocumentJson, string? responseSampleJson, string? ackSampleJson)
    {
        var gate = new List<string>();
        var request = new List<string>();
        var response = new List<string>();
        var ack = new List<string>();
        var general = new List<string>();
        string? renderedRequest = null;

        if (transactionType is not null && !KnownTransactionTypes.Contains(transactionType))
            general.Add($"Unknown transaction type '{transactionType}'.");
        if (builders.TryGet(portalEntity) is null)
            general.Add($"No input-document builder for portal entity '{portalEntity}'.");

        // Gate: compile; when a sample is pinned, evaluate and demand a strict boolean (D-R9-6).
        if (!string.IsNullOrWhiteSpace(gateExpr))
        {
            var syntax = mapping.ValidateSyntax(gateExpr);
            if (syntax is not null) gate.Add(syntax);
            else if (!string.IsNullOrWhiteSpace(sampleDocumentJson))
            {
                var result = mapping.Evaluate(gateExpr, sampleDocumentJson);
                if (!result.Ok) gate.Add(result.Error!);
                else if (result.OutputJson is not ("true" or "false"))
                    gate.Add($"Gate must return a boolean; sample evaluation produced {(result.OutputJson is null ? "nothing" : result.OutputJson.Length > 60 ? result.OutputJson[..60] + "…" : result.OutputJson)}.");
            }
        }

        // Request: compile; when a sample is pinned, it must evaluate and produce output.
        if (string.IsNullOrWhiteSpace(requestExpr))
            request.Add("Request mapping is required.");
        else
        {
            var syntax = mapping.ValidateSyntax(requestExpr);
            if (syntax is not null) request.Add(syntax);
            else if (!string.IsNullOrWhiteSpace(sampleDocumentJson))
            {
                var result = mapping.Evaluate(requestExpr, sampleDocumentJson);
                if (!result.Ok) request.Add(result.Error!);
                else if (result.OutputJson is null) request.Add("Request mapping produced no output against the pinned sample.");
                else renderedRequest = result.OutputJson;
            }
        }

        ValidateClosedSlot(mapping, response, "Response", responseExpr, required: true, responseSampleJson);
        ValidateClosedSlot(mapping, ack, "Ack", ackExpr, required: false, ackSampleJson);

        if (!string.IsNullOrWhiteSpace(candidateFilterName)
            && !filters.TryValidate(portalEntity, candidateFilterName, candidateFilterParams, out var filterError))
            general.Add(filterError!);

        var isValid = gate.Count + request.Count + response.Count + ack.Count + general.Count == 0;
        return new LnConfigValidationResultDto(isValid, gate, request, response, ack, general, renderedRequest);
    }

    private static void ValidateClosedSlot(ILnMappingService mapping, List<string> errors, string slot, string? expr, bool required, string? sampleJson)
    {
        if (string.IsNullOrWhiteSpace(expr))
        {
            if (required) errors.Add($"{slot} mapping is required.");
            return;
        }
        var syntax = mapping.ValidateSyntax(expr);
        if (syntax is not null) { errors.Add(syntax); return; }
        if (string.IsNullOrWhiteSpace(sampleJson)) return;

        var result = mapping.Evaluate(expr, sampleJson);
        if (!result.Ok) { errors.Add(result.Error!); return; }
        var (_, contractErrors) = LnClosedContract.Parse(result.OutputJson);
        errors.AddRange(contractErrors);
    }
}

public record ValidateOutboundIntegrationConfigCommand(ValidateOutboundIntegrationConfigRequest Body) : IRequest<LnConfigValidationResultDto>;

public class ValidateOutboundIntegrationConfigCommandHandler : IRequestHandler<ValidateOutboundIntegrationConfigCommand, LnConfigValidationResultDto>
{
    private readonly ILnMappingService _mapping;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    private readonly ICandidateFilterRegistry _filters;
    public ValidateOutboundIntegrationConfigCommandHandler(ILnMappingService mapping, ILnInputDocumentBuilderRegistry builders, ICandidateFilterRegistry filters)
    { _mapping = mapping; _builders = builders; _filters = filters; }

    public Task<LnConfigValidationResultDto> Handle(ValidateOutboundIntegrationConfigCommand request, CancellationToken ct)
    {
        var b = request.Body;
        return Task.FromResult(LnConfigValidationPipeline.Run(_mapping, _builders, _filters,
            transactionType: null, b.PortalEntity, b.EligibilityGateExpr, b.RequestMappingExpr, b.ResponseMappingExpr,
            b.AckMappingExpr, b.CandidateFilterName, b.CandidateFilterParams,
            b.SampleDocumentJson, b.ResponseSampleJson, b.AckSampleJson));
    }
}

public record SaveOutboundIntegrationConfigCommand(SaveOutboundIntegrationConfigRequest Body) : IRequest<Guid>;

public class SaveOutboundIntegrationConfigCommandHandler : IRequestHandler<SaveOutboundIntegrationConfigCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnMappingService _mapping;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    private readonly ICandidateFilterRegistry _filters;
    public SaveOutboundIntegrationConfigCommandHandler(IAppDbContext db, ICurrentUser user, ILnMappingService mapping,
        ILnInputDocumentBuilderRegistry builders, ICandidateFilterRegistry filters)
    { _db = db; _user = user; _mapping = mapping; _builders = builders; _filters = filters; }

    public async Task<Guid> Handle(SaveOutboundIntegrationConfigCommand request, CancellationToken ct)
    {
        var b = request.Body;
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");
        if (!Enum.TryParse<OutboundIntegrationKind>(b.Kind, ignoreCase: false, out var kind))
            throw new ValidationException($"Unknown integration kind '{b.Kind}' (Transaction | Document).");

        // R10 — wire formats: response Xml is live (generic XML→JSON normalizer); request Xml is a declared
        // placeholder until the JSON→XML serializer ships with the first XML-speaking transport.
        var requestFormat = string.IsNullOrWhiteSpace(b.RequestFormat) ? "Json" : b.RequestFormat;
        var responseFormat = string.IsNullOrWhiteSpace(b.ResponseFormat) ? "Json" : b.ResponseFormat;
        if (requestFormat is not ("Json" or "Xml")) throw new ValidationException("requestFormat must be Json or Xml.");
        if (responseFormat is not ("Json" or "Xml")) throw new ValidationException("responseFormat must be Json or Xml.");
        if (requestFormat == "Xml")
            throw new ValidationException("requestFormat=Xml is reserved: no XML request serializer is registered yet (arrives with the first XML-speaking transport).");

        // Connection tag must exist, belong to the tenant, and have a registered transport.
        if (b.ConnectionPointId is { } cpId)
        {
            var cp = await _db.ConnectionPoints.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == cpId && p.TenantId == tid && !p.IsDeleted, ct)
                ?? throw new ValidationException("Connection point not found.");
            if (!Connection.OutboundTransportCatalog.Available.Contains(cp.SystemType))
                throw new ValidationException($"No outbound transport is registered for connection type '{cp.SystemType}' yet — the connection can be saved, but configs cannot dispatch through it.");
        }

        var row = b.Id is { } id
            ? await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid && !c.IsDeleted, ct)
            : kind == OutboundIntegrationKind.Transaction
                ? await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.TenantId == tid && c.Kind == OutboundIntegrationKind.Transaction
                                              && c.TransactionType == b.TransactionType && !c.IsDeleted, ct)
                : await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.TenantId == tid && c.Kind == OutboundIntegrationKind.Document
                                              && c.PortalEntity == b.PortalEntity && c.AttachmentType == b.AttachmentType
                                              && !c.IsDeleted, ct);
        if (row is not null && row.Kind != kind)
            throw new ValidationException("Integration kind is immutable — delete and recreate to change it.");

        if (kind == OutboundIntegrationKind.Transaction)
        {
            if (string.IsNullOrWhiteSpace(b.TransactionType))
                throw new ValidationException("Transaction integrations require a transaction type.");
            // Validate against the row's PINNED sample (a request-body sample is not accepted at save — the
            // pinned snapshot is the trusted one, D-R9-18). Response/ack samples ARE editable via the request.
            var result = LnConfigValidationPipeline.Run(_mapping, _builders, _filters,
                b.TransactionType, b.PortalEntity, b.EligibilityGateExpr, b.RequestMappingExpr, b.ResponseMappingExpr,
                b.AckMappingExpr, b.CandidateFilterName, b.CandidateFilterParams,
                row?.SampleDocumentJson, b.ResponseSampleJson ?? row?.ResponseSampleJson, b.AckSampleJson ?? row?.AckSampleJson);
            if (!result.IsValid)
                throw new ValidationException(string.Join(" | ",
                    result.GeneralErrors.Concat(result.GateErrors).Concat(result.RequestErrors)
                          .Concat(result.ResponseErrors).Concat(result.AckErrors)));
        }
        else
        {
            // Document kind: no compiled builder / pinned-sample machinery — compile-check every expression;
            // response mapping optional (blank = code-owned parser fallback); closed contract does not apply
            // (the Document response contract is {pid}).
            if (string.IsNullOrWhiteSpace(b.TargetEntityName))
                throw new ValidationException("Document integrations require a target entity name.");
            var errors = new List<string>();
            foreach (var (label, expr) in new[]
            {
                ("gate", b.EligibilityGateExpr), ("request", b.RequestMappingExpr),
                ("mutate", b.MutateMappingExpr), ("response", b.ResponseMappingExpr), ("ack", b.AckMappingExpr),
            })
            {
                if (string.IsNullOrWhiteSpace(expr)) continue;
                var syntax = _mapping.ValidateSyntax(expr);
                if (syntax is not null) errors.Add($"{label}: {syntax}");
            }
            if (string.IsNullOrWhiteSpace(b.RequestMappingExpr)) errors.Add("request: mapping is required.");
            if (!string.IsNullOrWhiteSpace(b.ContextJson))
            {
                try { System.Text.Json.JsonDocument.Parse(b.ContextJson); }
                catch { errors.Add("context: not valid JSON."); }
            }
            if (!string.IsNullOrWhiteSpace(b.StaticHeadersJson))
            {
                try { System.Text.Json.JsonDocument.Parse(b.StaticHeadersJson); }
                catch { errors.Add("staticHeaders: not valid JSON."); }
            }
            if (errors.Count > 0) throw new ValidationException(string.Join(" | ", errors));
        }

        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new OutboundIntegrationConfig
            {
                TenantId = tid,
                Kind = kind,
                TransactionType = kind == OutboundIntegrationKind.Transaction ? b.TransactionType : null,
                // Creation NEVER opens dispatch (D-R9-2): Transaction rows are born Legacy (compiled builder
                // keeps serving); Document rows are born Held (no legacy path exists — Held = off).
                DispatchMode = kind == OutboundIntegrationKind.Transaction
                    ? OutboundDispatchMode.Legacy : OutboundDispatchMode.Held,
                GateVersion = 1,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            };
            _db.OutboundIntegrationConfigs.Add(row);
        }
        else
        {
            // gateVersion bumps on ANY gate/mapping/filter change (drives the backfill auto-prompt, D-R9-19).
            var changed = row.EligibilityGateExpr != b.EligibilityGateExpr
                          || row.RequestMappingExpr != b.RequestMappingExpr
                          || row.MutateMappingExpr != b.MutateMappingExpr
                          || row.ResponseMappingExpr != b.ResponseMappingExpr
                          || row.AckMappingExpr != b.AckMappingExpr
                          || row.CandidateFilterName != b.CandidateFilterName
                          || row.CandidateFilterParams != b.CandidateFilterParams;
            if (changed) row.GateVersion++;
            row.UpdatedBy = _user.UserCode;
            row.UpdatedOn = now;
        }

        row.ConnectionPointId = b.ConnectionPointId;
        row.PortalEntity = b.PortalEntity;
        row.AttachmentType = kind == OutboundIntegrationKind.Document ? NullIfBlank(b.AttachmentType) : null;
        row.TargetEntityName = NullIfBlank(b.TargetEntityName);
        row.ContextJson = NullIfBlank(b.ContextJson);
        row.EndpointPath = b.EndpointPath;
        row.HttpVerb = string.IsNullOrWhiteSpace(b.HttpVerb) ? "POST" : b.HttpVerb;
        row.MutatePath = NullIfBlank(b.MutatePath);
        row.MutateVerb = NullIfBlank(b.MutateVerb);
        row.DeletePath = NullIfBlank(b.DeletePath);
        row.DeleteVerb = NullIfBlank(b.DeleteVerb);
        row.StaticHeadersJson = NullIfBlank(b.StaticHeadersJson);
        row.RequestFormat = requestFormat;
        row.ResponseFormat = responseFormat;
        row.EligibilityGateExpr = b.EligibilityGateExpr;
        row.RequestMappingExpr = b.RequestMappingExpr;
        row.MutateMappingExpr = NullIfBlank(b.MutateMappingExpr);
        row.ResponseMappingExpr = NullIfBlank(b.ResponseMappingExpr);
        row.AckMappingExpr = b.AckMappingExpr;
        row.CandidateFilterName = b.CandidateFilterName;
        row.CandidateFilterParams = b.CandidateFilterParams;
        if (b.ResponseSampleJson is not null) row.ResponseSampleJson = b.ResponseSampleJson;
        if (b.AckSampleJson is not null) row.AckSampleJson = b.AckSampleJson;

        await _db.SaveChangesAsync(ct);
        return row.Id;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public class SaveOutboundIntegrationConfigValidator : AbstractValidator<SaveOutboundIntegrationConfigCommand>
{
    public SaveOutboundIntegrationConfigValidator()
    {
        RuleFor(x => x.Body.Kind).NotEmpty();
        RuleFor(x => x.Body.TransactionType).MaximumLength(60);
        RuleFor(x => x.Body.PortalEntity).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Body.AttachmentType).MaximumLength(50);
        RuleFor(x => x.Body.TargetEntityName).MaximumLength(100);
        RuleFor(x => x.Body.EndpointPath).NotEmpty().MaximumLength(400);
        RuleFor(x => x.Body.HttpVerb).Must(v => string.IsNullOrWhiteSpace(v) || v is "POST" or "PUT" or "PATCH")
            .WithMessage("httpVerb must be POST, PUT or PATCH.");
        RuleFor(x => x.Body.MutateVerb).Must(v => string.IsNullOrWhiteSpace(v) || v is "POST" or "PUT" or "PATCH")
            .WithMessage("mutateVerb must be POST, PUT or PATCH.");
        RuleFor(x => x.Body.DeleteVerb).Must(v => string.IsNullOrWhiteSpace(v) || v is "POST" or "PUT" or "PATCH" or "DELETE")
            .WithMessage("deleteVerb must be POST, PUT, PATCH or DELETE.");
        RuleFor(x => x.Body.RequestMappingExpr).NotEmpty();
        // Response mapping is required for Transaction kind (closed contract); optional for Document
        // (blank = code-owned parser fallback). Enforced in the handler where kind is parsed.
    }
}

/// <summary>D-R9-18 — run a REAL entity through the actual input-document builder and freeze the output on the config.</summary>
public record PinLnSampleDocumentCommand(Guid ConfigId, Guid EntityId) : IRequest<bool>;

public class PinLnSampleDocumentCommandHandler : IRequestHandler<PinLnSampleDocumentCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    public PinLnSampleDocumentCommandHandler(IAppDbContext db, ICurrentUser user, ILnInputDocumentBuilderRegistry builders)
    { _db = db; _user = user; _builders = builders; }

    public async Task<bool> Handle(PinLnSampleDocumentCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        var row = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;
        if (row.Kind != OutboundIntegrationKind.Transaction || row.TransactionType is null)
            throw new ValidationException("Sample pinning applies to Transaction integrations only.");

        var builder = _builders.TryGet(row.PortalEntity)
            ?? throw new ValidationException($"No input-document builder for portal entity '{row.PortalEntity}'.");
        var json = await builder.BuildJsonAsync(_db, request.EntityId, row.TransactionType, null, ct)
            ?? throw new ValidationException("Entity not found (or deleted) — pick a live entity to pin.");

        row.SampleDocumentJson = json;
        row.SampleBuilderVersion = builder.BuilderVersion;
        row.UpdatedBy = _user.UserCode;
        row.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>D-R9-17 + D-R9-21 — record the manual dry-post attestation. The system records; it does not verify.</summary>
public record AttestLnEndpointCommand(Guid ConfigId, AttestLnEndpointRequest Body) : IRequest<bool>;

public class AttestLnEndpointCommandHandler : IRequestHandler<AttestLnEndpointCommand, bool>
{
    /// <summary>The D-R9-21 confirmation line the checkbox stamps into verifiedNote.</summary>
    public const string PathConfirmationLine = "path confirmed against tenant Available-APIs export";

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public AttestLnEndpointCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<bool> Handle(AttestLnEndpointCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        if (string.IsNullOrWhiteSpace(request.Body.Note))
            throw new ValidationException("An attestation note is required (what was dry-posted, when, against which tenant).");

        var row = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;

        var note = request.Body.Note.Trim();
        if (request.Body.PathConfirmed && !note.Contains(PathConfirmationLine, StringComparison.OrdinalIgnoreCase))
            note = $"{note} [{PathConfirmationLine}]";

        row.VerifiedBy = _user.UserCode;
        row.VerifiedAt = DateTime.UtcNow;
        row.VerifiedNote = note.Length > 500 ? note[..500] : note;
        row.PathConfirmed = request.Body.PathConfirmed;
        row.UpdatedBy = _user.UserCode;
        row.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>
/// Tri-state transition (D-R9-2/11/17/21): → Dynamic demands attestation + pathConfirmed + a fresh pinned
/// sample + a green validation pipeline; → Held / → Legacy are always allowed (kill / rollback must never
/// be blocked by validation).
/// </summary>
public record SetOutboundDispatchModeCommand(Guid ConfigId, string Mode) : IRequest<bool>;

public class SetOutboundDispatchModeCommandHandler : IRequestHandler<SetOutboundDispatchModeCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnMappingService _mapping;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    private readonly ICandidateFilterRegistry _filters;
    public SetOutboundDispatchModeCommandHandler(IAppDbContext db, ICurrentUser user, ILnMappingService mapping,
        ILnInputDocumentBuilderRegistry builders, ICandidateFilterRegistry filters)
    { _db = db; _user = user; _mapping = mapping; _builders = builders; _filters = filters; }

    public async Task<bool> Handle(SetOutboundDispatchModeCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        if (!Enum.TryParse<OutboundDispatchMode>(request.Mode, ignoreCase: false, out var mode))
            throw new ValidationException($"Unknown dispatch mode '{request.Mode}' (Legacy | Dynamic | Held).");

        var row = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;

        // Document kind has no legacy compiled builder — Legacy is not a reachable mode for it.
        if (row.Kind == OutboundIntegrationKind.Document && mode == OutboundDispatchMode.Legacy)
            throw new ValidationException("Document integrations have no legacy path — use Held to stop dispatch.");

        if (mode == OutboundDispatchMode.Dynamic && row.Kind == OutboundIntegrationKind.Transaction)
        {
            var blockers = new List<string>();
            if (row.VerifiedAt is null) blockers.Add("attestation is not recorded (D-R9-17)");
            if (!row.PathConfirmed) blockers.Add("endpoint path is not confirmed against the tenant Available-APIs export (D-R9-21)");
            if (string.IsNullOrWhiteSpace(row.SampleDocumentJson)) blockers.Add("no sample document is pinned (D-R9-18)");
            else if (row.SampleBuilderVersion != LnInputDocumentVersions.For(row.PortalEntity)) blockers.Add("the pinned sample is stale — re-snapshot");

            var validation = LnConfigValidationPipeline.Run(_mapping, _builders, _filters,
                row.TransactionType, row.PortalEntity, row.EligibilityGateExpr, row.RequestMappingExpr,
                row.ResponseMappingExpr, row.AckMappingExpr, row.CandidateFilterName, row.CandidateFilterParams,
                row.SampleDocumentJson, row.ResponseSampleJson, row.AckSampleJson);
            if (!validation.IsValid) blockers.Add("save-time validation is not green");

            if (blockers.Count > 0)
                throw new ValidationException($"Cannot enable dynamic dispatch: {string.Join("; ", blockers)}.");
        }
        else if (mode == OutboundDispatchMode.Dynamic && row.Kind == OutboundIntegrationKind.Document)
        {
            // Parity with the pre-R10 IsEnabled toggle: expressions must at least compile before dispatch opens.
            var blockers = new List<string>();
            if (string.IsNullOrWhiteSpace(row.RequestMappingExpr)) blockers.Add("request mapping is empty");
            foreach (var (label, expr) in new[]
            {
                ("gate", row.EligibilityGateExpr), ("request", row.RequestMappingExpr),
                ("mutate", row.MutateMappingExpr), ("response", row.ResponseMappingExpr),
            })
            {
                if (string.IsNullOrWhiteSpace(expr)) continue;
                var syntax = _mapping.ValidateSyntax(expr);
                if (syntax is not null) blockers.Add($"{label} does not compile: {syntax}");
            }
            if (blockers.Count > 0)
                throw new ValidationException($"Cannot enable dispatch: {string.Join("; ", blockers)}.");
        }

        row.DispatchMode = mode;
        row.UpdatedBy = _user.UserCode;
        row.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Restore one expression slot (<c>request</c>|<c>response</c>|<c>ack</c>) to the repo default + re-stamp the seed hash.</summary>
public record RestoreLnDefaultExpressionCommand(Guid ConfigId, string Slot) : IRequest<bool>;

public class RestoreLnDefaultExpressionCommandHandler : IRequestHandler<RestoreLnDefaultExpressionCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnExpressionCatalog _catalog;
    public RestoreLnDefaultExpressionCommandHandler(IAppDbContext db, ICurrentUser user, ILnExpressionCatalog catalog)
    { _db = db; _user = user; _catalog = catalog; }

    public async Task<bool> Handle(RestoreLnDefaultExpressionCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        var row = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;
        if (row.Kind != OutboundIntegrationKind.Transaction || row.TransactionType is null)
            throw new ValidationException("Repo-default restore applies to Transaction integrations only.");
        var repo = _catalog.TryGet(row.TransactionType)
            ?? throw new ValidationException($"No repo default exists for transaction type '{row.TransactionType}'.");

        switch (request.Slot.ToLowerInvariant())
        {
            case "request":
                row.RequestMappingExpr = repo.RequestExpr;
                row.RequestMappingSeedHash = repo.RequestHash;
                break;
            case "response":
                row.ResponseMappingExpr = repo.ResponseExpr;
                row.ResponseMappingSeedHash = repo.ResponseHash;
                break;
            case "ack":
                row.AckMappingExpr = repo.AckExpr;
                row.AckMappingSeedHash = repo.AckHash;
                break;
            default:
                throw new ValidationException($"Unknown slot '{request.Slot}' (request | response | ack).");
        }

        row.GateVersion++;   // an expression change is a gate/mapping change — the backfill prompt must see it
        row.UpdatedBy = _user.UserCode;
        row.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Soft delete. Transaction kind = permanent rollback to the legacy builder (D-R9-2). Document kind
/// additionally UN-classifies its not-yet-pushed documents (clears <c>DocumentUpload.idmEntityType</c> where
/// no IDM pid exists — pushed documents keep the stamp so a later Delete push can still resolve).</summary>
public record DeleteOutboundIntegrationConfigCommand(Guid ConfigId) : IRequest<bool>;

public class DeleteOutboundIntegrationConfigCommandHandler : IRequestHandler<DeleteOutboundIntegrationConfigCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeleteOutboundIntegrationConfigCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<bool> Handle(DeleteOutboundIntegrationConfigCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        var row = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;

        var now = DateTime.UtcNow;
        if (row.Kind == OutboundIntegrationKind.Document && row.TargetEntityName is not null)
        {
            // Un-classify UNPUSHED documents stamped by this mapping (attachment filter composed in C# — an
            // inline `== null` OR on a null parameter translates inconsistently in EF and silently skips rows).
            var attachmentType = row.AttachmentType;
            var clearQuery = _db.DocumentUploads.IgnoreQueryFilters()
                .Where(d => !d.IsDeleted && d.TenantId == tid && d.Pid == null && d.IdmEntityType == row.TargetEntityName);
            if (attachmentType != null) clearQuery = clearQuery.Where(d => d.DocumentType == attachmentType);
            await clearQuery.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.IdmEntityType, (string?)null)
                .SetProperty(d => d.UpdatedBy, _user.UserCode)
                .SetProperty(d => d.UpdatedOn, now), ct);
        }

        row.IsDeleted = true;
        row.DeletedOn = now;
        row.DeletedBy = _user.UserCode;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
