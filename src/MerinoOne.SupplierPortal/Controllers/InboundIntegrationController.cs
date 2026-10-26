using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Inbound master-data ingestion consumed by Infor LN. Authenticated by the non-default <c>"ApiKey"</c>
/// scheme (X-APIKey header), authorized by the per-endpoint <c>Integration.Inbound.*</c> scope policy
/// (minted as a permission claim by the API-key auth handler). Rate-limited via the named <c>"inbound"</c>
/// partitioned policy. Thin — delegates to MediatR; the command handlers own company resolution,
/// share-group normalization, anti-spoof, the endpoint kill-switch, idempotency and the transactional
/// upsert + SyncLog/IntegrationError + endpoint session update.
/// </summary>
[ApiController]
[Route("api/integration/inbound")]
[Tags("Inbound Integration")]
[EnableRateLimiting("inbound")]
public class InboundIntegrationController : ControllerBase
{
    /// <summary>Optional idempotency header. When absent the handler hashes the canonical payload.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    private readonly IMediator _mediator;
    public InboundIntegrationController(IMediator mediator) => _mediator = mediator;

    [HttpPost("payment-terms")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundPaymentTerm)]
    [EndpointSummary("Push payment terms (Infor LN)")]
    [EndpointDescription(@"Upserts Payment Term master rows pushed by Infor LN.
Auth: X-APIKey scheme; the key must carry the `Integration.Inbound.PaymentTerm` scope and be bound to the source company the body resolves to.
Headers:
- **Idempotency-Key**: Optional — a replay with the same key (or identical body) is a no-op.
Body:
- **CompanyCode**: Infor LN logistic company (e.g. ""3000""); resolved to a company in the key's tenant and normalized to its share-group source (e.g. 3000 -> 2000).
- **Terms**: 1..1000 records (Code <=20, Description <=200, NetDays 0..365, no duplicate Code).
Behaviour: 200 + UpsertResultDto (per-row outcomes; partial failures flagged + an IntegrationError raised for operator retry); 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> PaymentTerms([FromBody] PushPaymentTermsRequest body, CancellationToken ct)
    {
        var bound = BoundCompanyIds();
        var key = IdempotencyKey();
        var data = await _mediator.Send(new UpsertPaymentTermsCommand(body, bound, key), ct);
        return Result<UpsertResultDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("delivery-terms")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundDeliveryTerm)]
    [EndpointSummary("Push delivery terms (Infor LN)")]
    [EndpointDescription(@"Upserts Delivery Term master rows pushed by Infor LN.
Auth: X-APIKey scheme; the key must carry the `Integration.Inbound.DeliveryTerm` scope and be bound to the source company the body resolves to.
Headers:
- **Idempotency-Key**: Optional — a replay with the same key (or identical body) is a no-op.
Body:
- **CompanyCode**: Infor LN logistic company; resolved + normalized to its share-group source.
- **Terms**: 1..1000 records (Code <=20, Description <=200, no duplicate Code).
Behaviour: 200 + UpsertResultDto; 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> DeliveryTerms([FromBody] PushDeliveryTermsRequest body, CancellationToken ct)
    {
        var bound = BoundCompanyIds();
        var key = IdempotencyKey();
        var data = await _mediator.Send(new UpsertDeliveryTermsCommand(body, bound, key), ct);
        return Result<UpsertResultDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("carriers")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundCarrier)]
    [EndpointSummary("Push carriers (Infor LN)")]
    [EndpointDescription(@"Upserts Carrier master rows pushed by Infor LN. The portal's ASN screen offers these as the shipment carrier; the ASN snapshots the carrier code.
Auth: X-APIKey scheme; the key must carry the `Integration.Inbound.Carrier` scope and be bound to the source company the body resolves to.
Headers:
- **Idempotency-Key**: Optional — a replay with the same key (or identical body) is a no-op.
Body:
- **CompanyCode**: Infor LN logistic company; resolved + normalized to its share-group source.
- **Carriers**: 1..1000 records { code, description?, isActive } (Code REQUIRED <=9, Description OPTIONAL <=50 — LN may push a carrier with no name, isActive defaults to true, no duplicate Code in a batch).
Upsert key = (source company, code), case-insensitive; only description and isActive are updated on an existing row. An unchanged row is reported Skipped.
Behaviour: 200 + UpsertResultDto (per-row Inserted/Updated/Skipped/Failed); 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> Carriers([FromBody] PushCarriersRequest body, CancellationToken ct)
    {
        var bound = BoundCompanyIds();
        var key = IdempotencyKey();
        var data = await _mediator.Send(new UpsertCarriersCommand(body, bound, key), ct);
        return Result<UpsertResultDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    // ---------------- Reference masters — tenant-scoped (no CompanyCode) ----------------

    [HttpPost("currencies")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundCurrency)]
    [EndpointSummary("Push currencies (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Currencies([FromBody] PushCurrenciesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertCurrenciesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("countries")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundCountry)]
    [EndpointSummary("Push countries (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Countries([FromBody] PushCountriesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertCountriesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("states")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundState)]
    [EndpointSummary("Push states (Infor LN)")]
    public async Task<Result<UpsertResultDto>> States([FromBody] PushStatesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertStatesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("cities")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundCity)]
    [EndpointSummary("Push cities (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Cities([FromBody] PushCitiesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertCitiesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("postal-codes")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundPostalCode)]
    [EndpointSummary("Push postal codes (Infor LN)")]
    public async Task<Result<UpsertResultDto>> PostalCodes([FromBody] PushPostalCodesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertPostalCodesCommand(body, IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    // ---------------- Inventory masters — company-scoped (CompanyCode) ----------------

    [HttpPost("units")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundUnit)]
    [EndpointSummary("Push units (Infor LN)")]
    public async Task<Result<UpsertResultDto>> Units([FromBody] PushUnitsRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertUnitsCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("item-groups")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundItemGroup)]
    [EndpointSummary("Push item groups (Infor LN)")]
    public async Task<Result<UpsertResultDto>> ItemGroups([FromBody] PushItemGroupsRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertItemGroupsCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("items")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundItem)]
    [EndpointSummary("Push items (Infor LN)")]
    [EndpointDescription(@"Upserts company-scoped Item master rows pushed by Infor LN (ICompanyScoped, ERP-fed). CompanyCode is resolved + normalized to the share-group source; upsert keyed on (sourceId, Code). Unit + item group are referenced by code (resolved within the source company).
Auth: X-APIKey scheme; key must carry `Integration.Inbound.Item` and be bound to the source company the body resolves to.
Headers:
- **Idempotency-Key**: Optional — a replay with the same key (or identical body) is a no-op.
Body: { companyCode, items:[{ code, description, unitCode?, itemGroupCode?, hsnCode?, isActive, isSerialized, isLotControlled, overShipTolerancePct? }] }.
- **isSerialized**: Optional (default false) — item is serial-controlled; ASN line capture then requires serial numbers.
- **isLotControlled**: Optional (default false) — item is lot/batch-controlled; ASN line capture then requires lot + expiry.
- isSerialized and isLotControlled are MUTUALLY EXCLUSIVE (an item is serial- or lot-controlled, not both); a row with both true fails as Failed (the rest of the batch still upserts).
- **overShipTolerancePct**: Optional (default null → 0) — item-master over-ship tolerance floor (%, 0–999.99). A SupplierItem override (Settings) wins; this is the fallback the ASN over-ship guard applies.
Behaviour: 200 + UpsertResultDto (per-row Inserted/Updated/Failed); 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> Items([FromBody] PushItemsRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertItemsCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("taxes")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundTax)]
    [EndpointSummary("Push tax codes (Infor LN)")]
    [EndpointDescription(@"Upserts company-shared Tax master rows pushed by Infor LN (Q6: ICompanyScoped, ERP-fed). CompanyCode is resolved + normalized to the share-group source; upsert keyed on (sourceId, Code). PO/invoice lines resolve taxId by code.
Auth: X-APIKey scheme; key must carry `Integration.Inbound.Tax` and be bound to the source company the body resolves to.
Body: { companyCode, taxes:[{ code, description, taxRate?, isActive }] }.
R6 override rule: an admin rate edit in the portal PINS the row (isRateOverridden) — the sync then SKIPS writing
taxRate for that row (the pinned rate wins) but ALWAYS updates lastSyncedRate to the pushed value, so the drift is
visible and the admin can reset the override back to the synced rate. Un-pinned rows take the pushed taxRate as before.
Behaviour: 200 + UpsertResultDto; 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> Taxes([FromBody] PushTaxesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertTaxesCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    // ---------------- Transactional inbound (R4 Module 5 / Increment D) — the ERP inbound loop ----------------

    [HttpPost("grn-status")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundGrn)]
    [EndpointSummary("Push goods-receipt status (Infor LN)")]
    [EndpointDescription(@"Pushes goods-receipt (GRN) approval status from Infor LN. GRN status is ERP-owned (no portal approval).
Auth: X-APIKey scheme; the key must carry the `Integration.Inbound.Grn` scope and be bound to the source company the body resolves to.
Headers:
- **Idempotency-Key**: Optional — a replay with the same key (or identical body) is a no-op.
Body:
- **CompanyCode**: Infor LN logistic company; resolved to a company in the key's tenant (no share-group normalization — GRNs belong to the literal company).
- **Receipts**: 1..1000 records { grnNumber, grnStatus (GrnNotApproved|GrnApproved|Rejected), erpSyncId?, receivedQty?, asnNumber?/asnErpRef?, issueReported?, erpCode? }.
Cascade: on a GRN transitioning INTO GrnApproved, when ALL GRNs covering that invoice are approved AND the invoice is Submitted with erpPostedAt NULL, the invoice post is enqueued on the outbox (deterministic key; idempotent; system-actor audit). A GrnApproved->NotApproved/Rejected reversal on an already-posted invoice raises an operator alert (no auto un-post).
Behaviour: 200 + UpsertGrnStatusResultDto (per-row outcome + auto-posts-enqueued + reverse-transition-alerts); 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertGrnStatusResultDto>> GrnStatus([FromBody] PushGrnStatusRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpsertGoodsReceiptStatusCommand(body, BoundCompanyIds(), IdempotencyKey()), ct);
        return Result<UpsertGrnStatusResultDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("payments")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundPayment)]
    [EndpointSummary("Push payments / remittance (Infor LN)")]
    [EndpointDescription(@"Writes payment / remittance rows pushed by Infor LN (payments originate in the ERP). The invoice is resolved by invoiceErpSyncId (preferred) else invoiceNumber.
Auth: X-APIKey scheme; key must carry `Integration.Inbound.Payment` and be bound to the resolved company.
Body:
- **CompanyCode**: Infor LN logistic company (resolved to the literal company).
- **Payments**: 1..1000 records { paymentReference, netPaid, invoiceErpSyncId?/invoiceNumber, paymentAmount?, tdsDeducted?, paymentDate?, paymentMode?, erpSyncId?, erpCode? }. Upsert key = (invoiceId, paymentReference).
Behaviour: 200 + UpsertResultDto; 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> Payments([FromBody] PushPaymentsRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertPaymentsCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("invoice-status")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundInvoiceStatus)]
    [EndpointSummary("Push invoice status (Infor LN)")]
    [EndpointDescription(@"Advances invoice status from Infor LN (Matched|MatchExceptions|Approved|Rejected|PartiallyPaid|Paid). The writer never regresses a portal-owned state (Draft/Submitted/Cancelled) and never advances a Rejected invoice (terminal for this writer — its per-PO-line reservation was already released; such rows are reported failed). Advancing TO Rejected releases the invoice's reservation atomically with the status flip.
Auth: X-APIKey scheme; key must carry `Integration.Inbound.InvoiceStatus` and be bound to the resolved company.
Body:
- **CompanyCode**: Infor LN logistic company (resolved to the literal company).
- **Invoices**: 1..1000 records { invoiceStatus, invoiceErpSyncId?/invoiceNumber, erpCode? }.
Behaviour: 200 + UpsertResultDto; 400 unknown company / non-advanceable status / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> InvoiceStatus([FromBody] PushInvoiceStatusRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertInvoiceStatusCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("erp-ack")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundErpAck)]
    [EndpointSummary("ERP acknowledgement / erpCode write-back (Infor LN)")]
    [EndpointDescription(@"The ERP acknowledges a Portal->ERP transaction and (on success) returns the entity's ERP code, written back to the matching record. Tenant-scoped (no CompanyCode).
Auth: X-APIKey scheme; key must carry `Integration.Inbound.ErpAck`.
Body:
- **Acks**: 1..1000 records { transactionType, portalRef (the deterministic outbox correlation id), success, erpCode (required on success), message?, erpCompany?, erpTransactionType?, erpDocumentNo? }.
Behaviour: on success resolves portalRef to exactly one outbox row of transactionType, writes erpCode (Supplier->SupCode, Asn->ASNNo, Invoice/Payment/change-line, etc.) and flips the row to Acked. Idempotent on re-ack; transactionType mismatch or missing row -> IntegrationError, no write (risk R17). R8/R11.2: on InvoicePost the optional erpCompany/erpTransactionType/erpDocumentNo are written to the Invoice (its Infor IDM eligibility gate); on AsnPost they are ACCEPTED but NOT stored — the ASN's IDM gate keys on erpCode alone (the composite columns were dropped; erpCompany always equals the ASN's own company code). Changing a gate field on an already-synced owner (the Invoice composite, or the ASN erpCode) enqueues an IDM document Update; a corrected value may be re-sent on an already-Acked row (re-stamped, outbox stays Acked). 200 + UpsertResultDto; 400 validation; 403 disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> ErpAck([FromBody] PushErpAckRequest body, CancellationToken ct)
        // Review S3 — pass the key's bound companies (anti-spoof), as the other three transactional endpoints do:
        // an ack may only stamp/erp-code a record whose company is in the key's bound set.
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertErpAckCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    // ---------------- Transactional document ingestion (R4 2026-06-23) — create/upsert live documents ----------------

    [HttpPost("purchase-orders")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundPo)]
    [EndpointSummary("Push purchase orders (Infor LN)")]
    [EndpointDescription(@"Creates/updates Purchase Orders (+ lines) pushed by Infor LN. PoNumber is the natural key within the resolved company; currency/payment-term/delivery-term/item/tax resolve by code (resolve-or-keep-snapshot). The owning supplier resolves from erpSupplierCode (matched on Supplier.ErpCode) OR supplierCode (matched on Supplier.SupplierCode); when both are present erpSupplierCode WINS, when neither is present the row is rejected (at least one required). The PO's seccode is the owning supplier's, so supplier users see it via RLS.
Auth: X-APIKey scheme; key must carry `Integration.Inbound.Po` and be bound to the resolved company.
Body: { companyCode, orders:[{ poNumber, supplierCode?, erpSupplierCode?, erpStatus?, poOrigin?, poDate, poType?, poStatus?, currencyCode?, paymentTerms?, deliveryTerms?, notes?, erpSyncId?, lines:[{ positionNo, sequenceNo, itemCode, itemDescription?, warehouse, orderUnit, orderQty, additionalQty?, priceUnit, price, discountPct, discountAmount, deliveryDate?, taxCode?, taxDescription?, taxAmount? }] }] }. supplierCode/erpSupplierCode: supply at least one.
poOrigin is the raw ERP-owned PO origin (e.g. the LN order origin / originating document indicator); it is stored verbatim on the PO header for tracking/reference only (ERP-owned, overwritten on the next inbound sync) and does not affect any portal state.
Warehouse (R13): warehouse is the receiving warehouse CODE, now PER LINE (a PO may span warehouses), and is REQUIRED on every line — a payload with any line omitting it is rejected with 400 for the whole batch. Each code resolves to an ACTIVE, NON-BASE CompanyAddress by ErpCode within the PO's company; on resolution the line stores the raw code, the address FK + a point-in-time snapshot. If ANY line's warehouse code does not resolve, the WHOLE PO row is HARD-FAILED (the PO is NOT created/updated). It is part of the canonical idempotency digest, so a PO re-pushed only to deliver a changed warehouse is NOT deduped away.
Field caps (400 on violation): erpStatus <=50, poOrigin <=50, paymentTermCode/deliveryTermCode <=20; per line itemCode REQUIRED <=50, warehouse REQUIRED <=50, orderUnit <=20, taxCode <=20.
Line item description and tax amount: itemDescription is OPTIONAL - when omitted and itemCode resolves to the Item master, the master's description is snapshotted onto the line at write time (an explicitly pushed description wins). taxAmount is OPTIONAL - the tax amount LN computed for the line, in the PO currency (decimal, up to 4 decimal places). It is an AMOUNT field, so like price and discountAmount it follows the qty rule: on an orderQty>0 push it REPLACES the stored amount as received (omitted/null means LN supplied none, so a full re-push that omits taxAmount clears a previously stored value); on an additionalQty push it is a signed DELTA added to the stored amount, exactly like price/discountAmount (an omitted/null delta leaves the stored amount unchanged; with nothing stored yet the delta becomes the amount); a 0/0 push is a no-op. It is informational (taxCode/taxDescription remain the link to the Tax master) and does not change the line net amount or the PO total. It is NOT a material change (it never re-arms the supplier's PO confirmation) but it IS part of the canonical idempotency digest used when no Idempotency-Key header is sent, so a PO re-pushed only to deliver or correct taxAmount is processed, not deduped away. The PO detail read returns it as the line's taxAmount.
Master soft-check: paymentTermCode/deliveryTermCode/itemCode/taxCode/orderUnit that do not resolve against their master (within the resolved company) do NOT fail the row — the PO is saved with the FK left null (snapshot kept), the row's UpsertResultDto entry carries a ""Warning — unresolved master codes: …"" advisory, and a standalone Failed Sync Log row records the drift for the operator. Note: an unresolved itemCode leaves ItemId null, so ASN serial/lot + over-ship guards cannot fire for that line until the Item master lands and the PO is re-pushed.
erpStatus is the raw ERP status, stored on the PO for tracking only (the portal PoStatus mapping is a later phase; this phase records the raw value but does not change PoStatus from it).
Lines: the STORAGE natural key within a PO is positionNo ALONE. sequenceNo is accepted but NOT stored as received — the portal ALWAYS stores sequenceNo=1, and multiple inbound lines sharing a positionNo are FOLDED into the single stored line for that position. orderQty (absolute, >=0) and additionalQty (signed delta, may be NEGATIVE to reduce) are MUTUALLY EXCLUSIVE per line: orderQty>0 REPLACES the stored qty; additionalQty!=0 ADDS the delta to the current qty; both 0 is a no-op (qty unchanged); both non-zero is rejected. A resolved NEGATIVE qty is rejected per-row; a revision BELOW the already-shipped quantity is ALLOWED (the line is flagged over-shipped and further ASNs are blocked, not refused — UC-ASN-10). NOTE: an identical repeated additionalQty push is deduped by the canonical-hash idempotency — send a distinct Idempotency-Key / erpSyncId per intentional add.
Behaviour: 200 + UpsertResultDto; 400 unknown company / missing line warehouse / neither supplier identifier supplied / orderQty and additionalQty both set / validation; per-row Failed for unknown supplier code or erpCode, unresolvable line warehouse code, or resolved qty negative; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> PurchaseOrders([FromBody] PushPurchaseOrdersRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertPurchaseOrdersCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("delivery-schedules")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundDeliverySchedule)]
    [EndpointSummary("Push delivery schedules (Infor LN)")]
    [EndpointDescription(@"Creates/updates PO delivery schedules (proposed dates) pushed by Infor LN. PO resolved by poNumber within the resolved company; upsert key = (PurchaseOrderId, ProposedDate).
Auth: X-APIKey scheme; key must carry `Integration.Inbound.DeliverySchedule` and be bound to the resolved company.
Body: { companyCode, schedules:[{ poNumber, proposedDate, timeWindow?, vehicleInfo?, scheduleStatus? }] }.
Behaviour: 200 + UpsertResultDto; 400 unknown company / unknown PO (per-row) / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> DeliverySchedules([FromBody] PushDeliverySchedulesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertDeliverySchedulesCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("goods-receipts")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundGrnReceipt)]
    [EndpointSummary("Push goods receipts / GRN rows (Infor LN)")]
    [EndpointDescription(@"Creates/updates goods-receipt (GRN) rows pushed by Infor LN, against a PO line resolved by (poNumber, poPositionNo). New GRNs land GrnNotApproved; the separate /grn-status endpoint then advances them (and triggers the invoice auto-post). GrnNumber is the natural key within the resolved company.
Auth: X-APIKey scheme; key must carry `Integration.Inbound.GrnReceipt` and be bound to the resolved company.
Body: { companyCode, receipts:[{ grnNumber, poNumber, poPositionNo, receivedQty, shortQty?, rejectedQty?, grnDate?, asnNumber?, erpSyncId? }] }.
Behaviour: 200 + UpsertResultDto; 400 unknown company / unknown PO line (per-row) / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> GoodsReceipts([FromBody] PushGoodsReceiptsRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertGoodsReceiptsCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    [HttpPost("asn-update")]
    [RequestSizeLimit(2_000_000)]
    [Authorize(AuthenticationSchemes = "ApiKey", Policy = Perm.IntegrationInboundAsnUpdate)]
    [EndpointSummary("Push ASN shipment status + received quantities (Infor LN)")]
    [EndpointDescription(@"Infor LN reports back on an ASN the portal already POSTED: LN's own shipment number, the shipment status, and per line the quantity LN received and the actual delivery date.
Auth: X-APIKey scheme; the key must carry the `Integration.Inbound.AsnUpdate` scope and be bound to the resolved company.
Headers:
- **Idempotency-Key**: Optional — a replay with the same key (or identical body) is a no-op.
Body (note the upper-case `ASN` array and the `supplierASN` key): { companyCode, ASN:[{ erpSupplierCode, supplierASN, shipment, status, lines?:[{ positionNo, receivedQty?, actualDeliveryDate?, poNumber? }] }] }. 1..1000 ASN records; erpSupplierCode/supplierASN/shipment <=50, status <=30, all required; positionNo > 0; receivedQty >= 0; no two lines of one ASN record may share the same (positionNo, poNumber).
- **CompanyCode**: Infor LN logistic company, resolved to the literal company in the key's tenant (no share-group normalization — an ASN belongs to its own company).
Matching: erpSupplierCode resolves the supplier (matched on Supplier.ErpCode, as for purchase orders); supplierASN is the portal's ASN number for that supplier in that company; the shipment is LN's own number for it.
- **shipment**: stamped onto the ASN's ERP code when the ASN has none yet (this makes the ASN's documents eligible for IDM sync). If the ASN already carries a different code the record FAILS with a shipment-mismatch error — it is never overwritten.
- **status**: stored VERBATIM as the ASN's ERP shipment status. When it reads InTransit, Delivered or Received (case-insensitive; spaces, hyphens and underscores ignored, so ""In Transit"" matches) the ASN's portal status is also set to that state; backward moves are allowed (ERP is the truth). Any other status (e.g. ""Closed"") is stored but leaves the portal status unchanged.
- **Posted ASNs only**: the ASN must be Submitted, InTransit, Delivered or Received; a Draft/Approved/Cancelled/etc. ASN FAILS the record.
- **lines**: optional (omit for a status-only update). Each line matches the ASN line at positionNo (the PO line position the ASN snapshotted). When one ASN spans several POs that reuse a position, send **poNumber** to disambiguate; without it an ambiguous position FAILS the record. receivedQty is an ABSOLUTE replacement (not a delta, rounded to 4 decimals); actualDeliveryDate is a date (any time part is dropped). An omitted (null) receivedQty/actualDeliveryDate leaves the stored value unchanged.
Atomic per ASN: every check for an ASN record (supplier, ASN, state, shipment, every line) is made before anything is written, so one bad line fails that whole ASN record and writes nothing for it. Other ASN records in the batch are independent. Records are applied in array order.
Failed rows: a business failure is a Failed row in the 200 response (RowResult.Code = supplierASN, Error = the reason), never a 4xx. Outcome is Updated when anything changed, otherwise Skipped.
Behaviour: 200 + UpsertResultDto; 400 unknown company / validation; 403 spoofed company or disabled endpoint; 401 invalid key.")]
    public async Task<Result<UpsertResultDto>> AsnUpdate([FromBody] PushAsnUpdatesRequest body, CancellationToken ct)
        => Result<UpsertResultDto>.Ok(await _mediator.Send(new UpsertAsnUpdatesCommand(body, BoundCompanyIds(), IdempotencyKey()), ct), HttpContext.TraceIdentifier);

    /// <summary>
    /// The key's bound source companies — every "tenantEntityId" claim minted by the API-key auth handler
    /// (Feature C — multi-company keys). The inbound write path requires the resolved incoming source to
    /// be in this set.
    /// </summary>
    private HashSet<Guid> BoundCompanyIds()
        => User.FindAll("tenantEntityId")
            .Select(c => Guid.TryParse(c.Value, out var g) ? (Guid?)g : null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToHashSet();

    private string? IdempotencyKey()
        => Request.Headers.TryGetValue(IdempotencyHeader, out var v) ? v.FirstOrDefault() : null;
}
