using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Documents;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R18 (2026-11-03) — the ASN Packing List (R19: preview GET + download POST that auto-attaches for supplier users). A separate controller (not an action on <see cref="AsnsController"/>)
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
    [EndpointSummary("ASN Packing List (PDF preview)")]
    [EndpointDescription(@"R18 / R19 — renders the ASN's Packing List as an A4 PDF for **preview**: the supplier's letterhead, the shipment facts
(packing list number / date, ASN, POs, LN shipment number, carrier, vehicle, tracking, e-way bill, IRN), the Ship From /
Ship To blocks and one row per ASN line with its serial or lot numbers and expiry. Carrier and the receiving warehouse are
the ones frozen on the ASN. This is a pure read: nothing is written and nothing is attached to the ASN.
Filters / params:
- **id**: Required — ASN GUID.
Availability (one shared rule): a supplier with ASN Confirmation Required = Yes can print once the buyer has **Approved**
the ASN and after it is posted (Submitted / InTransit / Delivered / Received); a supplier with it = No can print from
**Draft** and after posting. A PendingApproval, Rejected or Cancelled ASN never has a Packing List.
Seccode-scoped through the normal ASN read (an ASN the caller cannot see is a 404, never a leak).
Returns: application/pdf served **inline** (Content-Disposition: inline; filename 'PackingList-{asnNumber}.pdf') so a viewer can
display it; 404 if the ASN does not exist or is outside the caller's scope; 409 when the Packing List is not (or no longer)
available for the ASN's status. To download it (and, for a supplier user, auto-attach it to the ASN) use
POST api/asns/{id}/packing-list/download. Requires permission **Asn.Read**.")]
    public async Task<IActionResult> Get(Guid id, [FromServices] IPackingListPdfGenerator pdfGenerator, CancellationToken ct)
    {
        var packingList = await _mediator.Send(new GetPackingListQuery(id), ct);
        var bytes = pdfGenerator.Generate(packingList);

        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
        {
            FileName = $"PackingList-{packingList.AsnNumber}.pdf",
        }.ToString();
        return File(bytes, "application/pdf");
    }

    [HttpPost("{id:guid}/packing-list/download")]
    [Authorize(Policy = Perm.AsnRead)]
    [EndpointSummary("Download ASN Packing List (PDF) + auto-attach for supplier users")]
    [EndpointDescription(@"R19 — the **download** twin of the Packing List preview: renders the same PDF (same availability rules, same
404 / 409 behaviour) and returns it as an attachment (Content-Disposition: attachment; filename 'PackingList-{asnNumber}.pdf').
Side effect (supplier users only): the generated file is auto-attached to the ASN's attachment panel as a **system-generated**
document of type 'PackingSlip' — but only when that attachment slot is enabled for the ASN's supplier (active type + entity +
a policy). A repeat download REPLACES the earlier system-generated Packing List (a manual upload of the same type is never
touched). An internal / buyer / admin download never attaches. The attach is best-effort: a storage failure never fails the download.
The response header **X-Report-Attached** is 'true' when the file was attached to the ASN and 'false' otherwise.
Filters / params:
- **id**: Required — ASN GUID.
Returns: application/pdf attachment; 404 if the ASN does not exist or is outside the caller's scope; 409 when the Packing List is
not available for the ASN's status. Requires permission **Asn.Read**.")]
    public async Task<IActionResult> Download(
        Guid id,
        [FromServices] IPackingListPdfGenerator pdfGenerator,
        [FromServices] GeneratedReportAttacher attacher,
        [FromServices] ILogger<AsnPackingListController> logger,
        CancellationToken ct)
    {
        var packingList = await _mediator.Send(new GetPackingListQuery(id), ct);
        var bytes = pdfGenerator.Generate(packingList);
        var fileName = $"PackingList-{packingList.AsnNumber}.pdf";

        var attached = false;
        try
        {
            attached = await attacher.AttachAsync(
                DocumentOwnerTypes.Asn, id, GeneratedReportTypes.PackingSlip, fileName, bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Packing List for ASN {AsnId} was generated but could not be auto-attached.", id);
        }

        Response.Headers["X-Report-Attached"] = attached ? "true" : "false";
        return File(bytes, "application/pdf", fileName);
    }
}
