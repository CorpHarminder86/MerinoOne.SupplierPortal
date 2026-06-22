using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.Commands;
using MerinoOne.SupplierPortal.Application.Masters.Queries;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/masters")]
public class MastersController : ControllerBase
{
    private readonly IMediator _mediator;
    public MastersController(IMediator mediator) => _mediator = mediator;

    // ---------------- Delivery Terms ----------------

    [HttpGet("delivery-terms")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Delivery terms list")]
    [EndpointDescription(@"All delivery terms (Incoterms-style codes used on POs).
Filters / params:
- **isActive**: Optional — true to show only active, false only inactive, omit for all.
Returns: List<MasterItemDto>. Requires permission **Settings.Read**.")]
    public async Task<Result<List<MasterItemDto>>> ListDeliveryTerms([FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetDeliveryTermsQuery(isActive), ct);
        return Result<List<MasterItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("delivery-terms/{id:guid}")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Delivery term detail")]
    [EndpointDescription(@"Single delivery term by GUID.
Filters / params:
- **id**: Required — delivery term GUID.
Returns: MasterItemDto on success; 404 if not found. Requires permission **Settings.Read**.")]
    public async Task<Result<MasterItemDto>> GetDeliveryTerm(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetDeliveryTermByIdQuery(id), ct);
        return Result<MasterItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("delivery-terms")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Create delivery term")]
    [EndpointDescription(@"Creates a new delivery term row.
Body:
- **body**: CreateDeliveryTermRequest with Code + Description.
Returns: MasterItemDto on success; 400 on validation. Requires permission **Settings.Write**.")]
    public async Task<Result<MasterItemDto>> CreateDeliveryTerm([FromBody] CreateDeliveryTermRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateDeliveryTermCommand(body), ct);
        return Result<MasterItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("delivery-terms/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Update delivery term")]
    [EndpointDescription(@"Updates an existing delivery term.
Filters / params:
- **id**: Required — delivery term GUID.
- **body**: UpdateDeliveryTermRequest with revised Code/Description.
Returns: MasterItemDto on success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result<MasterItemDto>> UpdateDeliveryTerm(Guid id, [FromBody] UpdateDeliveryTermRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateDeliveryTermCommand(id, body), ct);
        return Result<MasterItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("delivery-terms/{id:guid}/deactivate")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Deactivate delivery term")]
    [EndpointDescription(@"Marks a delivery term inactive; preserved on historical POs.
Filters / params:
- **id**: Required — delivery term GUID.
Side effects:
- Flips IsActive=false; rows remain queryable for historical records.
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> DeactivateDeliveryTerm(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivateDeliveryTermCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    // ---------------- Payment Terms ----------------

    [HttpGet("payment-terms")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Payment terms list")]
    [EndpointDescription(@"All payment terms (e.g. Net30, Net60) referenced on POs + invoices.
Filters / params:
- **isActive**: Optional — true active only, false inactive only, omit for all.
Returns: List<PaymentTermDto>. Requires permission **Settings.Read**.")]
    public async Task<Result<List<PaymentTermDto>>> ListPaymentTerms([FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPaymentTermsQuery(isActive), ct);
        return Result<List<PaymentTermDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("payment-terms/{id:guid}")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Payment term detail")]
    [EndpointDescription(@"Single payment term by GUID.
Filters / params:
- **id**: Required — payment term GUID.
Returns: PaymentTermDto on success; 404 if not found. Requires permission **Settings.Read**.")]
    public async Task<Result<PaymentTermDto>> GetPaymentTerm(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPaymentTermByIdQuery(id), ct);
        return Result<PaymentTermDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("payment-terms")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Create payment term")]
    [EndpointDescription(@"Creates a new payment term row.
Body:
- **body**: CreatePaymentTermRequest with Code + DueDays + Description.
Returns: PaymentTermDto on success; 400 on validation. Requires permission **Settings.Write**.")]
    public async Task<Result<PaymentTermDto>> CreatePaymentTerm([FromBody] CreatePaymentTermRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreatePaymentTermCommand(body), ct);
        return Result<PaymentTermDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("payment-terms/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Update payment term")]
    [EndpointDescription(@"Updates an existing payment term.
Filters / params:
- **id**: Required — payment term GUID.
- **body**: UpdatePaymentTermRequest with revised Code / DueDays / Description.
Returns: PaymentTermDto on success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result<PaymentTermDto>> UpdatePaymentTerm(Guid id, [FromBody] UpdatePaymentTermRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdatePaymentTermCommand(id, body), ct);
        return Result<PaymentTermDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("payment-terms/{id:guid}/deactivate")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Deactivate payment term")]
    [EndpointDescription(@"Marks a payment term inactive; preserved on historical POs.
Filters / params:
- **id**: Required — payment term GUID.
Side effects:
- Flips IsActive=false; rows remain queryable for historical records.
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> DeactivatePaymentTerm(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivatePaymentTermCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    // ---------------- Taxes (R4 Module 6) ----------------

    [HttpGet("taxes")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Tax master list")]
    [EndpointDescription(@"All tax codes (company-shared master) used on PO / invoice lines.
Filters / params:
- **isActive**: Optional — true active only, false inactive only, omit for all.
Returns: List<TaxDto>. Requires permission **Settings.Read**.")]
    public async Task<Result<List<TaxDto>>> ListTaxes([FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetTaxesQuery(isActive), ct);
        return Result<List<TaxDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("taxes/{id:guid}")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Tax detail")]
    [EndpointDescription(@"Single tax code by GUID.
Filters / params:
- **id**: Required — tax GUID.
Returns: TaxDto on success; 404 if not found. Requires permission **Settings.Read**.")]
    public async Task<Result<TaxDto>> GetTax(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetTaxByIdQuery(id), ct);
        return Result<TaxDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("taxes")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Create tax")]
    [EndpointDescription(@"Creates a new tax code row.
Body:
- **body**: CreateTaxRequest with Code + Description (+ optional TaxRate).
Returns: TaxDto on success; 400 on validation; 409 if code exists. Requires permission **Settings.Write**.")]
    public async Task<Result<TaxDto>> CreateTax([FromBody] CreateTaxRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateTaxCommand(body), ct);
        return Result<TaxDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("taxes/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Update tax")]
    [EndpointDescription(@"Updates an existing tax code (Code immutable).
Filters / params:
- **id**: Required — tax GUID.
- **body**: UpdateTaxRequest with revised Description / TaxRate / IsActive.
Returns: TaxDto on success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result<TaxDto>> UpdateTax(Guid id, [FromBody] UpdateTaxRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateTaxCommand(id, body), ct);
        return Result<TaxDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("taxes/{id:guid}/deactivate")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Deactivate tax")]
    [EndpointDescription(@"Marks a tax code inactive; preserved on historical PO/invoice lines.
Filters / params:
- **id**: Required — tax GUID.
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> DeactivateTax(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivateTaxCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    // ---------------- Items ----------------

    [HttpGet("items")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Item master list")]
    [EndpointDescription(@"All item master rows used on PO lines.
Filters / params:
- **isActive**: Optional — true active only, false inactive only, omit for all.
- **search**: Optional — free-text on item code / description.
Returns: List<ItemDto>. Requires permission **Settings.Read**.")]
    public async Task<Result<List<ItemDto>>> ListItems([FromQuery] bool? isActive, [FromQuery] string? search, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetItemsQuery(isActive, search), ct);
        return Result<List<ItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("items/{id:guid}")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Item detail")]
    [EndpointDescription(@"Single item master row by GUID.
Filters / params:
- **id**: Required — item GUID.
Returns: ItemDto on success; 404 if not found. Requires permission **Settings.Read**.")]
    public async Task<Result<ItemDto>> GetItem(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetItemByIdQuery(id), ct);
        return Result<ItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("items")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Create item")]
    [EndpointDescription(@"Creates a new item master row.
Body:
- **body**: CreateItemRequest with ItemCode + Description + Unit.
Returns: ItemDto on success; 400 on validation. Requires permission **Settings.Write**.")]
    public async Task<Result<ItemDto>> CreateItem([FromBody] CreateItemRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateItemCommand(body), ct);
        return Result<ItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("items/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Update item")]
    [EndpointDescription(@"Updates an existing item master row.
Filters / params:
- **id**: Required — item GUID.
- **body**: UpdateItemRequest with revised Description / Unit.
Returns: ItemDto on success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result<ItemDto>> UpdateItem(Guid id, [FromBody] UpdateItemRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateItemCommand(id, body), ct);
        return Result<ItemDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("items/{id:guid}/deactivate")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Deactivate item")]
    [EndpointDescription(@"Marks an item inactive; preserved on historical POs.
Filters / params:
- **id**: Required — item GUID.
Side effects:
- Flips IsActive=false; rows remain queryable for historical records.
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> DeactivateItem(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivateItemCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
