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
    public async Task<Result<DashboardSummaryDto>> Summary(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetDashboardSummaryQuery(), ct);
        return Result<DashboardSummaryDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
