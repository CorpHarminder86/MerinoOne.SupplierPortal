using System.Text.Json;
using System.Text.Json.Nodes;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 — <see cref="ILnEligibilityService"/>: resolve the config row (tenant-explicit — workers have no
/// ambient <c>ICurrentUser</c>), build the input document on the CALLER's scoped <c>IAppDbContext</c>
/// (tracked root queries surface in-flight mutations via EF identity resolution at enqueue time),
/// apply overrides, evaluate strict-true through the shared engine.
/// </summary>
public sealed class LnEligibilityService : ILnEligibilityService
{
    private readonly IAppDbContext _db;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    private readonly ILnMappingService _mapping;
    private readonly ILogger<LnEligibilityService> _logger;

    public LnEligibilityService(IAppDbContext db, ILnInputDocumentBuilderRegistry builders,
        ILnMappingService mapping, ILogger<LnEligibilityService> logger)
    {
        _db = db;
        _builders = builders;
        _mapping = mapping;
        _logger = logger;
    }

    public async Task<LnGateVerdict> EvaluateAsync(Guid tenantId, string transactionType, Guid entityId,
        LnInputDocOverrides? overrides = null, CancellationToken ct = default)
    {
        var cfg = await _db.OutboundIntegrationConfigs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Kind == OutboundIntegrationKind.Transaction
                        && c.TransactionType == transactionType && !c.IsDeleted)
            .Select(c => new { c.DispatchMode, c.PortalEntity, c.EligibilityGateExpr, c.GateVersion })
            .FirstOrDefaultAsync(ct);

        // THE rule: no config, or Legacy mode, ⇒ no gate (legacy code-eligibility untouched).
        if (cfg is null || cfg.DispatchMode == OutboundDispatchMode.Legacy)
            return LnGateVerdict.NoConfig;

        // Dynamic/Held with a blank gate ⇒ no gate, but the gateVersion still stamps onto enqueued rows.
        if (string.IsNullOrWhiteSpace(cfg.EligibilityGateExpr))
            return new LnGateVerdict(false, true, cfg.GateVersion, null, cfg.DispatchMode.ToString());

        try
        {
            var builder = _builders.TryGet(cfg.PortalEntity);
            if (builder is null)
                return Ineligible(cfg.GateVersion, cfg.DispatchMode, $"no input-document builder for '{cfg.PortalEntity}'");

            var inputJson = await builder.BuildJsonAsync(_db, entityId, transactionType, null, ct);
            if (inputJson is null)
                return Ineligible(cfg.GateVersion, cfg.DispatchMode, "input document unavailable (entity missing or deleted)");

            inputJson = ApplyOverrides(inputJson, overrides);

            var result = _mapping.Evaluate(cfg.EligibilityGateExpr, inputJson);
            if (!result.Ok)
                return Ineligible(cfg.GateVersion, cfg.DispatchMode, $"gate evaluation error: {result.Error}");

            // STRICT-TRUE (D-R9-6): only the JSON literal true is eligible; anything else fails closed.
            return result.OutputJson == "true"
                ? new LnGateVerdict(true, true, cfg.GateVersion, null, cfg.DispatchMode.ToString())
                : Ineligible(cfg.GateVersion, cfg.DispatchMode,
                    result.OutputJson is null ? "gate returned nothing" :
                    result.OutputJson == "false" ? "gate returned false" :
                    $"gate returned non-boolean: {Truncate(result.OutputJson, 60)}");
        }
        catch (Exception ex)
        {
            // Fail closed, never throw — a broken gate skips a post; it must never break a business save.
            _logger.LogWarning(ex, "LN gate evaluation crashed for {Tx} entity {EntityId} (tenant {TenantId}).",
                transactionType, entityId, tenantId);
            return Ineligible(cfg.GateVersion, cfg.DispatchMode, $"gate evaluation error: {ex.Message}");
        }
    }

    /// <summary>Patch in-transaction facts over the DB-built document (today: the invoice GRN-coverage bool).</summary>
    private static string ApplyOverrides(string inputJson, LnInputDocOverrides? overrides)
    {
        if (overrides?.GrnCoverageSatisfied is not { } coverage) return inputJson;
        var node = JsonNode.Parse(inputJson);
        if (node is not JsonObject obj) return inputJson;
        obj["hasCoveringGrns"] = coverage || (obj["hasCoveringGrns"]?.GetValue<bool>() ?? false);
        obj["allCoveringGrnsApproved"] = coverage;
        return obj.ToJsonString(new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never });
    }

    private static LnGateVerdict Ineligible(int gateVersion, OutboundDispatchMode mode, string reason)
        => new(true, false, gateVersion, reason, mode.ToString());

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
