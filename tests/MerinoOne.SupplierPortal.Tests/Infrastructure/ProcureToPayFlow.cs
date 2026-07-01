using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MerinoOne.SupplierPortal.Tests.Infrastructure;

/// <summary>
/// Shared procure-to-pay flow scaffolding for the lifecycle suites (ASN→invoice, invoice lifecycle, GRN
/// auto-post + payment). Pushes a single-line PO inbound for a fresh tagged supplier (granting the Supplier
/// API user read+write on its seccode) and exposes the ids the rest of the chain needs, plus a simple
/// single-line ASN request builder.
/// </summary>
public static class ProcureToPayFlow
{
    public sealed record Setup(
        string Tag,
        SecurityTestHarness.SeededSupplier Supplier,
        Guid PoId,
        string PoNumber,
        Guid PoLineId,
        int PoPositionNo,
        string ItemCode,
        decimal OrderQty,
        decimal PriceUnit);

    /// <summary>
    /// Pushes a single-line PO (qty 10 @ price-unit 100, a plain non-serial/non-lot item) for a fresh tagged
    /// supplier, and returns the persisted ids. Grants the Supplier-role user read+write on the new seccode.
    /// By default the PO is CONFIRMED to Accepted (ship-gate open) so the lifecycle suites can ship immediately;
    /// pass <paramref name="confirm"/>=false to leave it at the ingested PoStatus (used by the gate suite to drive
    /// the supplier confirmation flow itself).
    /// </summary>
    public static async Task<Setup> SeedPoAsync(
        IntegrationTestFixture fx, decimal orderQty = 10m, decimal priceUnit = 100m, bool confirm = true)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var supplier = await fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);

        // A plain item (no serial/lot) so the ASN happy path needs no per-line capture.
        var item = await fx.CreateItemAsync($"P2P-{tag}");

        var inbound = fx.CreateInboundClient();
        var poNumber = $"PO-P2P-{tag}";
        const int positionNo = 10;
        var poBody = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(
                PoNumber: poNumber, SupplierCode: supplier.SupplierCode, PoDate: DateTime.UtcNow.Date,
                Lines: new[]
                {
                    new PoLineRecord(PositionNo: positionNo, SequenceNo: 1, ItemCode: item.ItemCode,
                        OrderUnit: "EA", OrderQty: orderQty, PriceUnit: priceUnit, Price: priceUnit * orderQty),
                },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        });
        var poResp = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", poBody);
        poResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: await poResp.Content.ReadAsStringAsync());

        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines)
            .FirstAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
        var line = po.Lines.Single(l => l.PositionNo == positionNo);

        // R4 (2026-06-26) — Phase 2 PO confirmation gate (§6.2): a Released PO under the default AcceptToShip mode
        // BLOCKS ASN creation. The procure-to-pay lifecycle suites are about the ASN→invoice→GRN→payment chain, not
        // the gate, so confirm the PO (→ Accepted, stamping acceptedAt) here as the supplier would before shipping —
        // this keeps the ship-gate open for SimpleAsn(). (The gate itself is covered by PoConfirmationPolicyTests +
        // the dedicated gate integration suite, which pass confirm:false to drive the supplier confirmation flow.)
        if (confirm)
        {
            po.PoStatus = PoStatus.Accepted;
            po.AcceptedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return new Setup(tag, supplier, po.Id, poNumber, line.Id, positionNo, item.ItemCode, orderQty, priceUnit);
    }

    /// <summary>
    /// Pushes an additional single-line PO for an EXISTING supplier routed to a given ship-to erpCode (default the
    /// fixture ship-to). Leaves the PO at Released (confirm via the supplier <c>/accept</c> to materialise schedules).
    /// Used by the from-schedule suite to build multi-PO same-ship-to (UC-AS-01) and cross-ship-to (UC-AS-02) cases.
    /// </summary>
    public static async Task<Setup> SeedPoForSupplierAsync(
        IntegrationTestFixture fx, SecurityTestHarness.SeededSupplier supplier, string? shipToErpCode = null,
        decimal orderQty = 10m, decimal priceUnit = 100m, string? itemCodeSuffix = null)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var item = await fx.CreateItemAsync($"P2P-{itemCodeSuffix ?? tag}");

        var inbound = fx.CreateInboundClient();
        var poNumber = $"PO-P2P-{tag}";
        const int positionNo = 10;
        var poBody = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(
                PoNumber: poNumber, SupplierCode: supplier.SupplierCode, PoDate: DateTime.UtcNow.Date,
                Lines: new[]
                {
                    new PoLineRecord(PositionNo: positionNo, SequenceNo: 1, ItemCode: item.ItemCode,
                        OrderUnit: "EA", OrderQty: orderQty, PriceUnit: priceUnit, Price: priceUnit * orderQty),
                },
                ShipToAddress: shipToErpCode ?? IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        });
        var poResp = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", poBody);
        poResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await poResp.Content.ReadAsStringAsync());

        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines)
            .FirstAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
        var line = po.Lines.Single(l => l.PositionNo == positionNo);
        return new Setup(tag, supplier, po.Id, poNumber, line.Id, positionNo, item.ItemCode, orderQty, priceUnit);
    }

    /// <summary>A single-line ASN against the seeded PO line, shipping the full ordered qty.</summary>
    public static CreateAsnRequest SimpleAsn(Setup s, decimal? shippedQty = null)
        => new(
            PurchaseOrderId: s.PoId, PurchaseOrderIds: null,
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1),
            TimeWindow: null, CarrierName: "Carrier", TrackingNumber: "TRK",
            VehicleNumber: null, DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(s.PoLineId, ShippedQty: shippedQty ?? s.OrderQty, BatchNumber: null, ExpiryDate: null),
            });

    // ════════════════════════════════════════════════════════════════════════════════════════════════════
    // R5 (TSD R5 Addendum §10) — ASN approval-lifecycle test helpers. The supplier-only `/submit` is gone;
    // an ASN reaches Submitted ONLY through Send-for-Approval → buyer Approve. These helpers drive that chain
    // so the re-timed checks (attachment at send-for-approval, over-ship guard at approve→submit) fire at
    // their NEW sites.
    // ════════════════════════════════════════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions HelperJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Assigns the PO's <c>BuyerUserId</c> (inbound POs are buyer-less) so the buyer approve/reject gate resolves.
    /// Defaults to the seeded fixture Buyer user (<c>sec-buyer-a</c>).
    /// </summary>
    public static async Task AssignBuyerAsync(IntegrationTestFixture fx, Guid poId, Guid? buyerUserId = null)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == poId);
        po.BuyerUserId = buyerUserId ?? SecurityTestHarness.BuyerUserId;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Drives the full Send-for-Approval → buyer Approve chain on a Draft ASN, returning the FINAL approve
    /// response (Submitted on success). The supplier sends for approval; the buyer (must be a mapped PO buyer —
    /// the caller should have called <see cref="AssignBuyerAsync"/>) approves, which runs the submit path.
    /// </summary>
    public static async Task<HttpResponseMessage> SubmitViaApprovalAsync(
        IntegrationTestFixture fx, HttpClient supplierClient, Guid asnId,
        bool acknowledgeMissingAttachments = false, string? overrideReason = null)
    {
        var send = await supplierClient.PostAsJsonAsync(
            $"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest(acknowledgeMissingAttachments));
        // If the attachment governance blocks/confirms, return that response (the caller asserts on it).
        if (send.StatusCode != HttpStatusCode.OK) return send;
        var sendBody = await ReadHelper<AsnDetailDto>(send);
        if (sendBody.ConfirmationRequired) return send;   // Warning confirm — not yet PendingApproval.

        var buyer = await SecurityTestHarness.ClientAsAsync(fx, SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        return await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest(overrideReason));
    }

    /// <summary>Create (PO-picker) → Send-for-Approval → Approve in one call; returns the final approve response.</summary>
    public static async Task<HttpResponseMessage> CreateAndSubmitAsync(
        IntegrationTestFixture fx, HttpClient supplierClient, Setup setup, decimal? shippedQty = null)
    {
        await AssignBuyerAsync(fx, setup.PoId);
        var create = await supplierClient.PostAsJsonAsync("/api/asns", SimpleAsn(setup, shippedQty));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await create.Content.ReadAsStringAsync());
        var asnId = (await ReadHelper<AsnDetailDto>(create)).Data!.Id;
        return await SubmitViaApprovalAsync(fx, supplierClient, asnId);
    }

    /// <summary>
    /// Materialises the PO's Delivery Schedules (one per line) by driving the supplier <c>/accept</c> endpoint —
    /// schedules are portal-created when a PO becomes shippable (§8.1). The PO must be in a Released/acceptable
    /// state (seed with <c>confirm: false</c>). Returns the created schedule ids keyed by PO line id.
    /// </summary>
    public static async Task<Dictionary<Guid, Guid>> AcceptAndGetScheduleIdsAsync(
        IntegrationTestFixture fx, HttpClient supplierClient, Guid poId)
    {
        var accept = await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{poId}/accept",
            new Contracts.PurchaseOrders.AcceptPoRequest());
        accept.StatusCode.Should().Be(HttpStatusCode.OK, because: await accept.Content.ReadAsStringAsync());
        return await GetScheduleIdsAsync(fx, poId);
    }

    /// <summary>Reads the active Delivery Schedule ids for a PO, keyed by PO line id (direct DB read).</summary>
    public static async Task<Dictionary<Guid, Guid>> GetScheduleIdsAsync(IntegrationTestFixture fx, Guid poId)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DeliverySchedules.IgnoreQueryFilters()
            .Where(s => s.PurchaseOrderId == poId && !s.IsDeleted)
            .ToDictionaryAsync(s => s.PurchaseOrderLineId, s => s.Id);
    }

    /// <summary>
    /// Seeds a SECOND ship-to <c>admin.CompanyAddress</c> under the fixture customer company with a unique erpCode,
    /// for the cross-ship-to (UC-AS-02) negative. Returns (addressId, erpCode). Idempotent by erpCode.
    /// </summary>
    public static async Task<(Guid AddressId, string ErpCode)> SeedSecondShipToAsync(IntegrationTestFixture fx, string tag)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var erpCode = $"DC-ALT-{tag}";
        var existing = await db.CompanyAddresses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.ErpCode == erpCode && !a.IsDeleted);
        if (existing is not null) return (existing.Id, erpCode);

        var id = Guid.NewGuid();
        db.CompanyAddresses.Add(new Domain.Entities.Admin.CompanyAddress
        {
            Id = id, TenantEntityId = IntegrationTestFixture.CompanyId, AddressName = $"Alt DC {tag}",
            ErpCode = erpCode, AddressType = "Shipping", AddressLine1 = "2 Alt Estate",
            City = "Pune", State = "Maharashtra", Pincode = "411001", Country = "India",
            IsActive = true, CreatedBy = "seed", CreatedOn = now,
        });
        await db.SaveChangesAsync();
        return (id, erpCode);
    }

    private static async Task<Result<T>> ReadHelper<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, HelperJson))!;
    }
}
