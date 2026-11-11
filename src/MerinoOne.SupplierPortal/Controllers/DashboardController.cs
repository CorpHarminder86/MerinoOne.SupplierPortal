using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Dashboard.Queries;
using MerinoOne.SupplierPortal.Contracts.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IMediator _mediator;
    public DashboardController(IMediator mediator) => _mediator = mediator;

    /// <summary>Dashboard KPI tiles + recent activity. Any authenticated user; counts are seccode-scoped.</summary>
    [HttpGet("summary")]
    [EndpointSummary("Dashboard summary")]
    [EndpointDescription(@"KPI tiles + recent activity for the current user's home dashboard.
Tiles:
- **Submitted invoices (30d)**: invoices created in the last 30 days that are awaiting a decision
  (Submitted / UnderReview / Matched / MatchExceptions), with the previous 30 days for the trend arrow.
- **Open delivery schedules**: schedules whose PO line still has quantity left to ship (orderQty - shippedQtyToDate > 0).
Recent activity: the 10 newest records across purchase orders, ASNs, invoices and payments.
Side effects:
- Seccode-scoped: counts reflect only entities the caller can see.
- Role-aware: privileged roles (SuperAdmin/Admin/Buyer/Finance) see organisation-wide tiles; supplier users see their own.
Returns: DashboardSummaryDto. Requires authentication only — no permission gate.")]
    public async Task<Result<DashboardSummaryDto>> Summary(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetDashboardSummaryQuery(), ct);
        return Result<DashboardSummaryDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
