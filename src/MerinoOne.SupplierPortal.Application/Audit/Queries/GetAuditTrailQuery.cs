using Dapper;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Audit;

namespace MerinoOne.SupplierPortal.Application.Audit.Queries;

/// <summary>
/// Returns the field-level audit trail for one entity row, newest first.
/// Uses raw Dapper against <c>audit.AuditEntry</c> (TSD §7.6) to stay decoupled from any
/// in-progress <c>IAppDbContext.AuditEntries</c> wiring.
/// </summary>
public record GetAuditTrailQuery(string EntityName, Guid EntityId) : IRequest<List<AuditEntryDto>>;

public class GetAuditTrailQueryHandler : IRequestHandler<GetAuditTrailQuery, List<AuditEntryDto>>
{
    private readonly ISqlConnectionFactory _sql;
    private readonly ICurrentUser _user;
    public GetAuditTrailQueryHandler(ISqlConnectionFactory sql, ICurrentUser user)
    {
        _sql = sql;
        _user = user;
    }

    public async Task<List<AuditEntryDto>> Handle(GetAuditTrailQuery request, CancellationToken ct)
    {
        // SECURITY: this raw-Dapper read bypasses the EF AuditEntry query filter, so it MUST tenant-scope itself
        // explicitly (else cross-tenant IDOR — a Settings.Read admin in tenant A could read any tenant's
        // OldValue/NewValue by entityName+GUID). Fail-closed: a null caller tenant matches nothing.
        const string sql = @"
SELECT auditEntryId  AS Id,
       auditEntrySeq AS Seq,
       entityName    AS EntityName,
       entityId      AS EntityId,
       operation     AS Operation,
       fieldName     AS FieldName,
       oldValue      AS OldValue,
       newValue      AS NewValue,
       changedBy     AS ChangedBy,
       changedOn     AS ChangedOn
  FROM [audit].[AuditEntry]
 WHERE entityName = @EntityName
   AND entityId   = @EntityId
   AND tenantId   = @TenantId
 ORDER BY changedOn DESC;";

        await using var cn = await _sql.OpenAsync(ct);
        var rows = await cn.QueryAsync<AuditEntryDto>(new CommandDefinition(
            sql,
            new { request.EntityName, request.EntityId, TenantId = _user.TenantId },
            cancellationToken: ct));
        return rows.AsList();
    }
}
