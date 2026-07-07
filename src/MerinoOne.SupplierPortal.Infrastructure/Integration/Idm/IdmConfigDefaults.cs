using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// Resolves the <c>config.acl</c>/<c>config.entityName</c> values every snapshot injects for the mapping
/// expressions to read. R10: the source is the unified <c>OutboundIntegrationConfig</c> row's
/// <c>contextJson</c> (Document kind, matched by <c>targetEntityName</c>) — per-integration context,
/// replacing the R8 tenant-wide <c>IDM.Item.Create</c> transport row. The pre-R8 C# literals remain the
/// fallback when the row/keys are missing.
/// </summary>
internal static class IdmConfigDefaults
{
    private const string FallbackAcl = "Public";
    private const string FallbackEntityName = "MDS_GenericDocument";

    public static async Task<(string Acl, string EntityName)> ResolveAsync(
        IAppDbContext db, Guid tenantId, string targetEntityName, CancellationToken ct)
    {
        // Several rows may share one targetEntityName (D7) — a row that actually DEFINES a context wins,
        // ties broken by Seq for determinism.
        var contextJson = await db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Kind == OutboundIntegrationKind.Document
                        && c.TargetEntityName == targetEntityName && !c.IsDeleted)
            .OrderBy(c => c.ContextJson == null ? 1 : 0).ThenBy(c => c.Seq)
            .Select(c => c.ContextJson)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(contextJson)) return (FallbackAcl, FallbackEntityName);
        try
        {
            using var doc = JsonDocument.Parse(contextJson);
            var acl = doc.RootElement.TryGetProperty("acl", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
            var entityName = doc.RootElement.TryGetProperty("entityName", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
            return (
                string.IsNullOrWhiteSpace(acl) ? FallbackAcl : acl!,
                string.IsNullOrWhiteSpace(entityName) ? FallbackEntityName : entityName!);
        }
        catch (JsonException)
        {
            return (FallbackAcl, FallbackEntityName);   // malformed context → safe fallbacks (save validates, but belt+braces)
        }
    }
}
