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
    [EndpointSummary("Global search")]
    [EndpointDescription(@"Cross-module global search across POs, ASNs, GRs, invoices, payments, and suppliers.
Filters / params:
- **q**: Required — free-text query string.
- **module**: Optional — restrict to one module (e.g. ""invoices"", ""purchase-orders"").
- **from**: Optional — earliest CreatedOn to include.
- **to**: Optional — latest CreatedOn to include.
- **limit**: Optional — max rows to return (default 50).
Side effects:
- Seccode-scoped: non-privileged users see only results within their data domain.
Returns: List<SearchResultDto> with module + entity-id + display text. Any authenticated user.")]
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
