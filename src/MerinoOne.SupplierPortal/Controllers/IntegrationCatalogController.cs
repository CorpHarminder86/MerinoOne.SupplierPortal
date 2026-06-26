using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Read-only catalog of the external inbound integration endpoints, consumed by the in-app developer-docs
/// page (/integrations/docs). Kept separate from <see cref="InboundIntegrationController"/> (which is the
/// external, X-APIKey-authed surface) so this stays JWT/permission-gated. The interactive try-it reference is
/// the filtered Scalar at /integration-docs.
/// </summary>
[ApiController]
[Authorize]
[Route("api/integration")]
public class IntegrationCatalogController : ControllerBase
{
    [HttpGet("catalog")]
    [Authorize(Policy = "Integration.Read")]
    public Result<List<IntegrationEndpointDocDto>> Catalog()
        => Result<List<IntegrationEndpointDocDto>>.Ok(IntegrationCatalog.All.ToList(), HttpContext.TraceIdentifier);
}

/// <summary>
/// Single source of truth for the partner-facing inbound endpoint docs. Each entry's <c>Scope</c> MUST be a
/// member of <c>ApiKeyScopes.Allowed</c> — a Development startup guard in Program.cs asserts the two sets are
/// equal, so adding an 11th scoped endpoint (or removing one) without updating this catalog fails fast.
/// </summary>
public static class IntegrationCatalog
{
    private const string Base = "api/integration/inbound";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string Json(object sample) => JsonSerializer.Serialize(sample, JsonOpts);

    public static readonly IReadOnlyList<IntegrationEndpointDocDto> All = new[]
    {
        // ── Company-scoped (body carries CompanyCode; key needs bound source companies) ──
        new IntegrationEndpointDocDto("Payment terms", "Integration.Inbound.PaymentTerm", "POST", $"{Base}/payment-terms", true,
            "Upsert Payment Term master rows for the resolved source company.",
            Json(new { companyCode = "3000", terms = new[] { new { code = "N30", description = "Net 30 days", netDays = 30, isActive = true } } })),
        new IntegrationEndpointDocDto("Delivery terms", "Integration.Inbound.DeliveryTerm", "POST", $"{Base}/delivery-terms", true,
            "Upsert Delivery Term master rows for the resolved source company.",
            Json(new { companyCode = "3000", terms = new[] { new { code = "FOB", description = "Free on board", isActive = true } } })),
        new IntegrationEndpointDocDto("Units", "Integration.Inbound.Unit", "POST", $"{Base}/units", true,
            "Upsert unit-of-measure master rows for the resolved source company.",
            Json(new { companyCode = "3000", units = new[] { new { code = "KG", description = "Kilogram", unitType = "Mass", isoCode = "KGM", decimalPlaces = 3, conversionFactor = 1, baseUnitCode = (string?)null, isActive = true } } })),
        new IntegrationEndpointDocDto("Item groups", "Integration.Inbound.ItemGroup", "POST", $"{Base}/item-groups", true,
            "Upsert item-group master rows for the resolved source company.",
            Json(new { companyCode = "3000", itemGroups = new[] { new { code = "RAW", description = "Raw materials", isActive = true } } })),
        new IntegrationEndpointDocDto("Items", "Integration.Inbound.Item", "POST", $"{Base}/items", true,
            "Upsert item master rows (unit + group referenced by code) for the resolved source company. isSerialized / isLotControlled are LN-fed control flags (default false) that drive ASN serial / lot capture; they are mutually exclusive (an item is serial- or lot-controlled, not both). overShipTolerancePct (optional, default 0) is the LN-fed item-master over-ship tolerance floor (%, 0–999.99); a SupplierItem override (Settings) wins.",
            Json(new { companyCode = "3000", items = new[] { new { code = "ITM-00001", description = "Sample item", unitCode = "KG", itemGroupCode = "RAW", hsnCode = "39021000", isActive = true, isSerialized = false, isLotControlled = false, overShipTolerancePct = 5.0 } } })),
        new IntegrationEndpointDocDto("Taxes", "Integration.Inbound.Tax", "POST", $"{Base}/taxes", true,
            "Upsert tax-code master rows (company-shared) for the resolved source company. PO/invoice lines resolve taxId by code.",
            Json(new { companyCode = "3000", taxes = new[] { new { code = "GST18", description = "GST 18%", taxRate = 18.0, isActive = true } } })),

        // ── Tenant-scoped (no CompanyCode; bound to the key's tenant) ──
        new IntegrationEndpointDocDto("Currencies", "Integration.Inbound.Currency", "POST", $"{Base}/currencies", false,
            "Upsert tenant-scoped Currency master rows (no company code).",
            Json(new { records = new[] { new { code = "USD", description = "US Dollar", isoCode = "USD", symbol = "$", decimalPlaces = 2, isActive = true } } })),
        new IntegrationEndpointDocDto("Countries", "Integration.Inbound.Country", "POST", $"{Base}/countries", false,
            "Upsert tenant-scoped Country master rows (currency referenced by code).",
            Json(new { records = new[] { new { code = "IN", description = "India", isoCode2 = "IN", isoCode3 = "IND", telephoneCode = "91", currencyCode = "INR", isActive = true } } })),
        new IntegrationEndpointDocDto("States", "Integration.Inbound.State", "POST", $"{Base}/states", false,
            "Upsert tenant-scoped State master rows (country referenced by code).",
            Json(new { records = new[] { new { code = "MH", description = "Maharashtra", countryCode = "IN", isoCode = "MH", isActive = true } } })),
        new IntegrationEndpointDocDto("Cities", "Integration.Inbound.City", "POST", $"{Base}/cities", false,
            "Upsert tenant-scoped City master rows (country/state referenced by code).",
            Json(new { records = new[] { new { code = "MH-MUM", description = "Mumbai", countryCode = "IN", stateCode = "MH", isActive = true } } })),
        new IntegrationEndpointDocDto("Postal codes", "Integration.Inbound.PostalCode", "POST", $"{Base}/postal-codes", false,
            "Upsert tenant-scoped Postal Code master rows (country/state/city referenced by code).",
            Json(new { records = new[] { new { code = "400001", area = "Fort", countryCode = "IN", stateCode = "MH", cityCode = "MH-MUM", isActive = true } } })),

        // ── Transactional (R4 Module 5 / Increment D) — the ERP inbound loop ──
        new IntegrationEndpointDocDto("GRN status", "Integration.Inbound.Grn", "POST", $"{Base}/grn-status", true,
            "Push goods-receipt approval status (ERP-owned). On all-covering-GRNs-approved, auto-posts the invoice via the outbox.",
            Json(new { companyCode = "3000", receipts = new[] { new { grnNumber = "GRN-3000-000001", grnStatus = "GrnApproved", erpSyncId = "LN-GRN-1", receivedQty = 100.0, asnNumber = "ASN-0001", issueReported = (string?)null, erpCode = (string?)null } } })),
        new IntegrationEndpointDocDto("Payments", "Integration.Inbound.Payment", "POST", $"{Base}/payments", true,
            "Write payment / remittance rows from the ERP (invoice resolved by erpSyncId/invoiceNumber).",
            Json(new { companyCode = "3000", payments = new[] { new { paymentReference = "PAY-3000-000001", netPaid = 2500.0, invoiceNumber = "INV-0001", paymentAmount = 2500.0, tdsDeducted = 0.0, paymentMode = "NEFT", erpSyncId = "LN-PAY-1", erpCode = (string?)null } } })),
        new IntegrationEndpointDocDto("Invoice status", "Integration.Inbound.InvoiceStatus", "POST", $"{Base}/invoice-status", true,
            "Advance invoice status (Matched/MatchExceptions/Approved/Rejected/PartiallyPaid/Paid). Never regresses a portal-owned state.",
            Json(new { companyCode = "3000", invoices = new[] { new { invoiceStatus = "Paid", invoiceNumber = "INV-0001", erpCode = (string?)null } } })),
        new IntegrationEndpointDocDto("ERP ack", "Integration.Inbound.ErpAck", "POST", $"{Base}/erp-ack", false,
            "ERP acknowledgement + erpCode write-back for a Portal->ERP transaction (tenant-scoped; resolves portalRef -> outbox row).",
            Json(new { acks = new[] { new { transactionType = "AsnPost", portalRef = "<deterministic-outbox-key>", success = true, erpCode = "ASN-LN-0001", message = (string?)null } } })),

        // ── Transactional document ingestion (R4 2026-06-23) — create/upsert the live PO / delivery schedule / GRN ──
        new IntegrationEndpointDocDto("Purchase orders", "Integration.Inbound.Po", "POST", $"{Base}/purchase-orders", true,
            "Create/upsert Purchase Orders (+ lines) for the resolved company. Owning supplier resolved by erpSupplierCode (matched on Supplier.ErpCode) OR supplierCode (matched on Supplier.SupplierCode) — when both are sent erpSupplierCode wins; at least one is required. Currency/term/item/tax resolved by code.",
            Json(new { companyCode = "3000", orders = new[] { new { poNumber = "PO-3000-000001", erpSupplierCode = "ERP-S0001", supplierCode = "S0001", poDate = "2026-06-23", poType = "Material", poStatus = "Released", currencyCode = "INR", paymentTerms = "Net 30", deliveryTerms = "FOB", paymentTermCode = "NET30", deliveryTermCode = "FOB", notes = "Sample PO", erpSyncId = "LN-PO-1", lines = new[] { new { positionNo = 10, sequenceNo = 1, itemCode = "ITM-00001", itemDescription = "Sample item", orderUnit = "KG", orderQty = 100.0, priceUnit = 50.0, price = 5000.0, discountPct = 0.0, discountAmount = 0.0, deliveryDate = "2026-07-15", taxCode = "GST18", taxDescription = "GST 18%" } } } } })),
        new IntegrationEndpointDocDto("Delivery schedules", "Integration.Inbound.DeliverySchedule", "POST", $"{Base}/delivery-schedules", true,
            "Create/upsert PO delivery schedules (proposed dates) for the resolved company. PO resolved by poNumber.",
            Json(new { companyCode = "3000", schedules = new[] { new { poNumber = "PO-3000-000001", proposedDate = "2026-07-15", timeWindow = "09:00-12:00", vehicleInfo = (string?)null, scheduleStatus = "Proposed" } } })),
        new IntegrationEndpointDocDto("Goods receipts", "Integration.Inbound.GrnReceipt", "POST", $"{Base}/goods-receipts", true,
            "Create/upsert goods-receipt (GRN) rows against PO lines (resolved by poNumber + poPositionNo). New rows land GrnNotApproved; /grn-status then advances them.",
            Json(new { companyCode = "3000", receipts = new[] { new { grnNumber = "GRN-3000-000001", poNumber = "PO-3000-000001", poPositionNo = 10, receivedQty = 100.0, shortQty = 0.0, rejectedQty = 0.0, grnDate = "2026-07-16", asnNumber = (string?)null, erpSyncId = "LN-GRN-1" } } })),
    };
}
