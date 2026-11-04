using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R18 (2026-11-03) — the ASN Packing List download. A separate controller (not an action on <see cref="AsnsController"/>)
/// because it returns a file, not the <c>Result&lt;T&gt;</c> envelope every action there returns; same route prefix and
/// same read policy as the ASN detail it is printed from.
/// </summary>
[ApiController]
[Authorize]
[Route("api/asns")]
public class AsnPackingListController : ControllerBase
{
    private readonly IMediator _mediator;
    public AsnPackingListController(IMediator mediator) => _mediator = mediator;

    [HttpGet("{id:guid}/packing-list")]
    [Authorize(Policy = Perm.AsnRead)]
    [EndpointSummary("ASN Packing List (PDF)")]
    [EndpointDescription(@"R18 — renders the ASN's Packing List as an A4 PDF: the supplier's letterhead, the shipment facts
(packing list number / date, ASN, POs, LN shipment number, carrier, vehicle, tracking, e-way bill, IRN), the Ship From /
Ship To blocks and one row per ASN line with its serial or lot numbers and expiry. Carrier and the receiving warehouse are
the ones frozen on the ASN; nothing is written.
Filters / params:
- **id**: Required — ASN GUID.
Availability (one shared rule): a supplier with ASN Confirmation Required = Yes can print once the buyer has **Approved**
the ASN and after it is posted (Submitted / InTransit / Delivered / Received); a supplier with it = No can print from
**Draft** and after posting. A PendingApproval, Rejected or Cancelled ASN never has a Packing List.
Seccode-scoped through the normal ASN read (an ASN the caller cannot see is a 404, never a leak).
Returns: application/pdf file named 'PackingList-{asnNumber}.pdf'; 404 if the ASN does not exist or is outside the
caller's scope; 409 when the Packing List is not (or no longer) available for the ASN's status. Requires permission **Asn.Read**.")]
    public async Task<IActionResult> Get(Guid id, [FromServices] IPackingListPdfGenerator pdfGenerator, CancellationToken ct)
    {
        var packingList = await _mediator.Send(new GetPackingListQuery(id), ct);
        return File(pdfGenerator.Generate(packingList), "application/pdf", $"PackingList-{packingList.AsnNumber}.pdf");
    }
}
