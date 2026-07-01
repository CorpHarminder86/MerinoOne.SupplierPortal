using MediatR;
using MerinoOne.SupplierPortal.Application.Audit.Queries;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/audit")]
public class AuditController : ControllerBase
{
    private readonly IMediator _mediator;
    public AuditController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Field-level audit trail for one entity row. Gated on <c>Settings.Read</c> (admins only).
    /// </summary>
    [HttpGet("{entityName}/{id:guid}")]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("Audit trail")]
    [EndpointDescription(@"Returns field-level change history (Before/After values) captured by the audit interceptor.
Filters / params:
- **entityName**: Required — entity table name (e.g. ""Supplier"", ""PurchaseOrder"").
- **id**: Required — entity primary key.
Returns: List<AuditEntryDto> ordered by timestamp; 404 if no rows. Requires permission **Settings.Read** (admins only).")]
    public async Task<Result<List<AuditEntryDto>>> Trail(string entityName, Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetAuditTrailQuery(entityName, id), ct);
        return Result<List<AuditEntryDto>>.Ok(data, HttpContext.TraceIdentifier);
    }
}
