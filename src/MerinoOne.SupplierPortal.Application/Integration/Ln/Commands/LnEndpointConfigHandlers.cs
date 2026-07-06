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

public record GetLnEndpointConfigsQuery : IRequest<IReadOnlyList<LnEndpointConfigDto>>;

public class GetLnEndpointConfigsQueryHandler : IRequestHandler<GetLnEndpointConfigsQuery, IReadOnlyList<LnEndpointConfigDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnExpressionCatalog _catalog;
    public GetLnEndpointConfigsQueryHandler(IAppDbContext db, ICurrentUser user, ILnExpressionCatalog catalog)
    { _db = db; _user = user; _catalog = catalog; }

    public async Task<IReadOnlyList<LnEndpointConfigDto>> Handle(GetLnEndpointConfigsQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var rows = await _db.LnEndpointConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tid && !c.IsDeleted)
            .OrderBy(c => c.TransactionType)
            .ToListAsync(ct);

        return rows.Select(c =>
        {
            var repo = _catalog.TryGet(c.TransactionType);
            return new LnEndpointConfigDto(
                c.Id, c.Seq, c.TransactionType, c.PortalEntity, c.EndpointPath, c.HttpVerb,
                c.DispatchMode.ToString(), c.EligibilityGateExpr, c.RequestMappingExpr, c.ResponseMappingExpr,
                c.AckMappingExpr, c.CandidateFilterName, c.CandidateFilterParams, c.GateVersion,
                HasSample: !string.IsNullOrWhiteSpace(c.SampleDocumentJson),
                c.SampleBuilderVersion,
                SampleStale: !string.IsNullOrWhiteSpace(c.SampleDocumentJson)
                             && c.SampleBuilderVersion != LnInputDocumentVersions.For(c.PortalEntity),
                c.SampleDocumentJson, c.ResponseSampleJson, c.AckSampleJson,
                RequestDrifted: repo is not null && _catalog.Hash(c.RequestMappingExpr) != repo.RequestHash,
                ResponseDrifted: repo is not null && _catalog.Hash(c.ResponseMappingExpr) != repo.ResponseHash,
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

public record ValidateLnEndpointConfigCommand(ValidateLnEndpointConfigRequest Body) : IRequest<LnConfigValidationResultDto>;

public class ValidateLnEndpointConfigCommandHandler : IRequestHandler<ValidateLnEndpointConfigCommand, LnConfigValidationResultDto>
{
    private readonly ILnMappingService _mapping;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    private readonly ICandidateFilterRegistry _filters;
    public ValidateLnEndpointConfigCommandHandler(ILnMappingService mapping, ILnInputDocumentBuilderRegistry builders, ICandidateFilterRegistry filters)
    { _mapping = mapping; _builders = builders; _filters = filters; }

    public Task<LnConfigValidationResultDto> Handle(ValidateLnEndpointConfigCommand request, CancellationToken ct)
    {
        var b = request.Body;
        return Task.FromResult(LnConfigValidationPipeline.Run(_mapping, _builders, _filters,
            transactionType: null, b.PortalEntity, b.EligibilityGateExpr, b.RequestMappingExpr, b.ResponseMappingExpr,
            b.AckMappingExpr, b.CandidateFilterName, b.CandidateFilterParams,
            b.SampleDocumentJson, b.ResponseSampleJson, b.AckSampleJson));
    }
}

public record SaveLnEndpointConfigCommand(SaveLnEndpointConfigRequest Body) : IRequest<Guid>;

public class SaveLnEndpointConfigCommandHandler : IRequestHandler<SaveLnEndpointConfigCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnMappingService _mapping;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    private readonly ICandidateFilterRegistry _filters;
    public SaveLnEndpointConfigCommandHandler(IAppDbContext db, ICurrentUser user, ILnMappingService mapping,
        ILnInputDocumentBuilderRegistry builders, ICandidateFilterRegistry filters)
    { _db = db; _user = user; _mapping = mapping; _builders = builders; _filters = filters; }

    public async Task<Guid> Handle(SaveLnEndpointConfigCommand request, CancellationToken ct)
    {
        var b = request.Body;
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");

        var row = b.Id is { } id
            ? await _db.LnEndpointConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid && !c.IsDeleted, ct)
            : await _db.LnEndpointConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tid && c.TransactionType == b.TransactionType && !c.IsDeleted, ct);

        // Validate against the row's PINNED sample (a request-body sample is not accepted at save — the pinned
        // snapshot is the trusted one, D-R9-18). Response/ack samples ARE editable via the request.
        var result = LnConfigValidationPipeline.Run(_mapping, _builders, _filters,
            b.TransactionType, b.PortalEntity, b.EligibilityGateExpr, b.RequestMappingExpr, b.ResponseMappingExpr,
            b.AckMappingExpr, b.CandidateFilterName, b.CandidateFilterParams,
            row?.SampleDocumentJson, b.ResponseSampleJson ?? row?.ResponseSampleJson, b.AckSampleJson ?? row?.AckSampleJson);
        if (!result.IsValid)
            throw new ValidationException(string.Join(" | ",
                result.GeneralErrors.Concat(result.GateErrors).Concat(result.RequestErrors)
                      .Concat(result.ResponseErrors).Concat(result.AckErrors)));

        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new LnEndpointConfig
            {
                TenantId = tid,
                TransactionType = b.TransactionType,
                DispatchMode = LnDispatchMode.Legacy,   // creation NEVER changes dispatch (D-R9-2)
                GateVersion = 1,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            };
            _db.LnEndpointConfigs.Add(row);
        }
        else
        {
            // gateVersion bumps on ANY gate/mapping/filter change (drives the Phase B backfill auto-prompt, D-R9-19).
            var changed = row.EligibilityGateExpr != b.EligibilityGateExpr
                          || row.RequestMappingExpr != b.RequestMappingExpr
                          || row.ResponseMappingExpr != b.ResponseMappingExpr
                          || row.AckMappingExpr != b.AckMappingExpr
                          || row.CandidateFilterName != b.CandidateFilterName
                          || row.CandidateFilterParams != b.CandidateFilterParams;
            if (changed) row.GateVersion++;
            row.UpdatedBy = _user.UserCode;
            row.UpdatedOn = now;
        }

        row.PortalEntity = b.PortalEntity;
        row.EndpointPath = b.EndpointPath;
        row.HttpVerb = string.IsNullOrWhiteSpace(b.HttpVerb) ? "POST" : b.HttpVerb;
        row.EligibilityGateExpr = b.EligibilityGateExpr;
        row.RequestMappingExpr = b.RequestMappingExpr;
        row.ResponseMappingExpr = b.ResponseMappingExpr;
        row.AckMappingExpr = b.AckMappingExpr;
        row.CandidateFilterName = b.CandidateFilterName;
        row.CandidateFilterParams = b.CandidateFilterParams;
        if (b.ResponseSampleJson is not null) row.ResponseSampleJson = b.ResponseSampleJson;
        if (b.AckSampleJson is not null) row.AckSampleJson = b.AckSampleJson;

        await _db.SaveChangesAsync(ct);
        return row.Id;
    }
}

public class SaveLnEndpointConfigValidator : AbstractValidator<SaveLnEndpointConfigCommand>
{
    public SaveLnEndpointConfigValidator()
    {
        RuleFor(x => x.Body.TransactionType).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Body.PortalEntity).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Body.EndpointPath).NotEmpty().MaximumLength(400);
        RuleFor(x => x.Body.HttpVerb).Must(v => string.IsNullOrWhiteSpace(v) || v is "POST" or "PUT" or "PATCH")
            .WithMessage("httpVerb must be POST, PUT or PATCH.");
        RuleFor(x => x.Body.RequestMappingExpr).NotEmpty();
        RuleFor(x => x.Body.ResponseMappingExpr).NotEmpty();
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
        var row = await _db.LnEndpointConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;

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

        var row = await _db.LnEndpointConfigs.IgnoreQueryFilters()
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
public record SetLnDispatchModeCommand(Guid ConfigId, string Mode) : IRequest<bool>;

public class SetLnDispatchModeCommandHandler : IRequestHandler<SetLnDispatchModeCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILnMappingService _mapping;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    private readonly ICandidateFilterRegistry _filters;
    public SetLnDispatchModeCommandHandler(IAppDbContext db, ICurrentUser user, ILnMappingService mapping,
        ILnInputDocumentBuilderRegistry builders, ICandidateFilterRegistry filters)
    { _db = db; _user = user; _mapping = mapping; _builders = builders; _filters = filters; }

    public async Task<bool> Handle(SetLnDispatchModeCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        if (!Enum.TryParse<LnDispatchMode>(request.Mode, ignoreCase: false, out var mode))
            throw new ValidationException($"Unknown dispatch mode '{request.Mode}' (Legacy | Dynamic | Held).");

        var row = await _db.LnEndpointConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;

        if (mode == LnDispatchMode.Dynamic)
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
        var row = await _db.LnEndpointConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;
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

/// <summary>Soft delete = permanent rollback to the legacy builder for this transaction type (D-R9-2).</summary>
public record DeleteLnEndpointConfigCommand(Guid ConfigId) : IRequest<bool>;

public class DeleteLnEndpointConfigCommandHandler : IRequestHandler<DeleteLnEndpointConfigCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeleteLnEndpointConfigCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<bool> Handle(DeleteLnEndpointConfigCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        var row = await _db.LnEndpointConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConfigId && c.TenantId == tid && !c.IsDeleted, ct);
        if (row is null) return false;

        row.IsDeleted = true;
        row.DeletedOn = DateTime.UtcNow;
        row.DeletedBy = _user.UserCode;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
