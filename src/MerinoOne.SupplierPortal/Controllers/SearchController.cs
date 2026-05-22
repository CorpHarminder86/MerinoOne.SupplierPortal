using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Search.Queries;
using MerinoOne.SupplierPortal.Contracts.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/search")]
public class SearchController : ControllerBase
{
    private readonly IMediator _mediator;
    public SearchController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Cross-module global search. Any authenticated user; results are seccode-scoped for
    /// non-privileged users via <see cref="GlobalSearchQuery"/>.
    /// </summary>
    [HttpGet]
    public async Task<Result<List<SearchResultDto>>> Search(
        [FromQuery] string q,
        [FromQuery] string? module = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GlobalSearchQuery(q, module, from, to, limit), ct);
        return Result<List<SearchResultDto>>.Ok(data, HttpContext.TraceIdentifier);
    }
}
