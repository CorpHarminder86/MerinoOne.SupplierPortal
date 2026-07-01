using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R5 — TSD R5 Addendum §9 / §10 / UC-AS-01..02 / UC-AP-01..06. The new ASN-from-schedule create + approval
/// lifecycle on REAL SQL, through the real host:
/// <list type="bullet">
///   <item>UC-AS-01 — a multi-PO ASN across POs sharing one ship-to.</item>
///   <item>UC-AS-02 — cross-ship-to selection is blocked.</item>
///   <item>UC-AP-01 — Send-for-Approval creates a Pending AsnApproval session + routes to the PO buyer.</item>
///   <item>UC-AP-02 — the attachment check fires at Send-for-Approval (mandatory blocks, ASN stays Draft).</item>
///   <item>UC-AP-03 — Approve runs the submit path (over-ship guard at submit; consumed; Submitted).</item>
///   <item>UC-AP-04 — Reject (mandatory reason) returns the ASN to the supplier; no balance consumed.</item>
///   <item>UC-AP-05 — balance lost between approval and submit → submit fails; approval stands; not Submitted.</item>
///   <item>UC-AP-06 — any one of multiple buyers approves.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnApprovalFlowTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string AsnEntity = "Asn";

    private readonly IntegrationTestFixture _fx;
    public AsnApprovalFlowTests(IntegrationTestFixture fx) => _fx = fx;

    public async Task InitializeAsync() { if (_fx.DbAvailable) await _fx.ClearPoliciesAsync(); }
    public async Task DisposeAsync() { if (_fx.DbAvailable) await _fx.ClearPoliciesAsync(); }

    // ════════════════════════════ §9 — ASN from schedule ════════════════════════════

    // ── UC-AS-01 — Multi-line ASN across POs (same ship-to) ──────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_AS_01_Multi_PO_same_ship_to_creates_one_asn()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Two POs for ONE supplier, BOTH routed to the default fixture ship-to.
        var poA = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, orderQty: 10m);
        var poB = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, orderQty: 20m);
        var schedA = await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplierClient, poA.PoId);
        var schedB = await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplierClient, poB.PoId);

        var req = new CreateAsnFromScheduleRequest(
            ScheduleIds: new[] { schedA[poA.PoLineId], schedB[poB.PoLineId] },
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1));
        var resp = await supplierClient.PostAsJsonAsync("/api/asns/from-schedule", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));

        var asn = (await Read<AsnDetailDto>(resp)).Data!;
        asn.AsnStatus.Should().Be(nameof(AsnStatus.Draft));
        asn.PurchaseOrderId.Should().BeNull(because: "the deprecated header PO is unused on a schedule-built ASN (UC-AS-03)");
        asn.ShipToAddressId.Should().Be(IntegrationTestFixture.ShipToAddressId, because: "header grouped by ship-to (UC-AS-03)");
        asn.Lines.Should().HaveCount(2);
        asn.Lines.Select(l => l.PurchaseOrderId).Should().BeEquivalentTo(new[] { poA.PoId, poB.PoId },
            because: "lines reference different POs sharing one ship-to (UC-AS-01)");
        // Ship qty defaults to each line's remaining balance (§9.2).
        asn.Lines.Single(l => l.PurchaseOrderId == poA.PoId).ShippedQty.Should().Be(10m);
        asn.Lines.Single(l => l.PurchaseOrderId == poB.PoId).ShippedQty.Should().Be(20m);

        // NO balance consumed at create (§10.4).
        (await ShippedToDate(poA.PoLineId)).Should().Be(0m);
        (await ShippedToDate(poB.PoLineId)).Should().Be(0m);
    }

    // ── UC-AS-02 — Cross-ship-to selection blocked ───────────────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_AS_02_cross_ship_to_blocked()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var (_, altErp) = await ProcureToPayFlow.SeedSecondShipToAsync(_fx, tag);
        var poDefault = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, orderQty: 10m);
        var poAlt = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, shipToErpCode: altErp, orderQty: 10m);
        var schedDefault = await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplierClient, poDefault.PoId);
        var schedAlt = await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplierClient, poAlt.PoId);

        var req = new CreateAsnFromScheduleRequest(
            ScheduleIds: new[] { schedDefault[poDefault.PoLineId], schedAlt[poAlt.PoLineId] },
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1));
        var resp = await supplierClient.PostAsJsonAsync("/api/asns/from-schedule", req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        (await Read<AsnDetailDto>(resp)).Errors.Should()
            .Contain(e => e.Contains("ship-to", StringComparison.OrdinalIgnoreCase),
                because: "an ASN cannot mix ship-to addresses (UC-AS-02)");
    }

    // ════════════════════════════ §10 — approval lifecycle ════════════════════════════

    // ── UC-AP-01 — Send for Approval creates a session ───────────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_AP_01_send_for_approval_creates_session()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplierClient) = await NewDraftWithBuyerAsync();

        var send = await supplierClient.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));
        var detail = (await Read<AsnDetailDto>(send)).Data!;
        detail.AsnStatus.Should().Be(nameof(AsnStatus.PendingApproval));
        detail.Approval.Should().NotBeNull();
        detail.Approval!.Status.Should().Be(nameof(AsnApprovalStatus.Pending));
        detail.Approval.SubmittedBy.Should().Be(SecurityTestHarness.Users.Supplier);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.AsnApprovals.IgnoreQueryFilters().CountAsync(a => a.AsnId == asnId && !a.IsDeleted))
            .Should().Be(1, because: "Send-for-Approval creates exactly one Pending session (UC-AP-01)");
    }

    // ── UC-AP-02 — Attachment check at Send-for-Approval (mandatory blocks; stays Draft) ─────────────────
    [SkippableFact]
    public async Task UC_AP_02_attachment_check_at_send_for_approval()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Policy: TestCertificate Mandatory on the ASN entity (tenant default).
        await _fx.ClearPoliciesAsync();
        await _fx.SeedPoliciesAsync(new AttachmentGovernanceHarness.PolicySpec(
            AsnEntity, "TestCertificate", AttachmentRequirement.Mandatory));

        var (asnId, _, supplierClient) = await NewDraftWithBuyerAsync();

        // Send without the certificate → blocked with the mandatory message; ASN stays Draft (never reaches buyer).
        var send = await supplierClient.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(send));
        (await Read<AsnDetailDto>(send)).Errors.Should()
            .Contain(e => e.Contains("mandatory attachment") && e.Contains("TestCertificate"));
        await AssertStatus(asnId, AsnStatus.Draft);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.AsnApprovals.IgnoreQueryFilters().AnyAsync(a => a.AsnId == asnId && !a.IsDeleted))
            .Should().BeFalse(because: "a blocked Send-for-Approval creates NO session (UC-AP-02)");
    }

    // ── UC-AP-03 — Approve → Submit (over-ship guard at Submit) ──────────────────────────────────────────
    [SkippableFact]
    public async Task UC_AP_03_approve_then_submit_consumes_balance()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplierClient) = await NewDraftWithBuyerAsync(orderQty: 10m);
        await SendOkAsync(supplierClient, asnId);

        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));
        var detail = (await Read<AsnDetailDto>(approve)).Data!;
        detail.AsnStatus.Should().Be(nameof(AsnStatus.Submitted), because: "Approve runs the submit path (UC-AP-03)");
        detail.Approval!.Status.Should().Be(nameof(AsnApprovalStatus.Approved));
        detail.Approval.DecisionBy.Should().Be(SecurityTestHarness.Users.Buyer);
        detail.DraftInvoiceId.Should().NotBeNull(because: "the draft invoice is created at submit");

        (await ShippedToDate(setup.PoLineId)).Should().Be(10m, because: "the over-ship guard consumes balance at Submit (§10.4)");
    }

    // ── §20 — Approve notifies the submitting supplier (audit/notification touchpoint) ───────────────────
    [SkippableFact]
    public async Task Approve_notifies_the_submitting_supplier()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplierClient) = await NewDraftWithBuyerAsync(orderQty: 10m);
        await SendOkAsync(supplierClient, asnId);

        // Give the submitting supplier user an e-mail so the best-effort notification enqueues a row.
        const string supplierEmail = "supplier-approve@test.com";
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var u = await db.AppUsers.IgnoreQueryFilters()
                .FirstAsync(x => x.UserCode == SecurityTestHarness.Users.Supplier);
            u.Email = supplierEmail;
            await db.SaveChangesAsync();
        }

        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));

        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var mail = await db.EmailOutbox.IgnoreQueryFilters()
                .Where(m => m.ToEmail == supplierEmail && m.TemplateKey == "AsnApproval" && m.Subject.Contains("approved"))
                .OrderByDescending(m => m.CreatedOn)
                .FirstOrDefaultAsync();
            mail.Should().NotBeNull(because: "Approve notifies the supplier who submitted the ASN (§20)");
        }
    }

    // ── UC-AP-04 — Reject returns to supplier (mandatory reason; no balance consumed) ────────────────────
    [SkippableFact]
    public async Task UC_AP_04_reject_returns_to_supplier()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplierClient) = await NewDraftWithBuyerAsync(orderQty: 10m);
        await SendOkAsync(supplierClient, asnId);

        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);

        // Reject without a reason → 400 (reason mandatory).
        var noReason = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/reject", new RejectAsnRequest(""));
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: "a rejection reason is mandatory");

        var reject = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/reject", new RejectAsnRequest("Wrong carrier"));
        reject.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(reject));
        var detail = (await Read<AsnDetailDto>(reject)).Data!;
        detail.AsnStatus.Should().Be(nameof(AsnStatus.Rejected));
        detail.Approval!.Status.Should().Be(nameof(AsnApprovalStatus.Rejected));
        detail.Approval.Reason.Should().Be("Wrong carrier");

        (await ShippedToDate(setup.PoLineId)).Should().Be(0m, because: "no balance was consumed on the rejected ASN (UC-AP-04)");

        // The supplier edits the rejected ASN → it returns to Draft.
        var edit = await supplierClient.PutAsJsonAsync($"/api/asns/{asnId}", new UpdateAsnRequest(
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2), TimeWindow: null, CarrierName: "Right carrier",
            TrackingNumber: null, VehicleNumber: null, DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest> { new(setup.PoLineId, ShippedQty: 5m, BatchNumber: null, ExpiryDate: null) }));
        edit.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(edit));
        (await Read<AsnDetailDto>(edit)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.Draft),
            because: "editing a Rejected ASN returns it to Draft (§10.1)");
    }

    // ── UC-AP-05 — Balance lost between approval and submit → submit fails; not Submitted ────────────────
    [SkippableFact]
    public async Task UC_AP_05_balance_lost_between_approval_and_submit()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplierClient) = await NewDraftWithBuyerAsync(orderQty: 10m);
        await SendOkAsync(supplierClient, asnId);

        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        // Simulate a concurrent ASN consuming the whole line balance AFTER this ASN is PendingApproval.
        await ConsumeBalanceAsync(setup.PoLineId, 10m);

        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(approve));
        (await Read<AsnDetailDto>(approve)).Errors.Should()
            .Contain(e => e.Contains("Ship qty exceeds order qty plus over-ship tolerance."),
                because: "the guard returns 0 rows at submit — balance lost (UC-AP-05)");

        // The shipment did not proceed — the ASN stays PendingApproval (the approval flip rolled back with the guard).
        await AssertStatus(asnId, AsnStatus.PendingApproval);
        (await ShippedToDate(setup.PoLineId)).Should().Be(10m, because: "only the concurrent consumer's 10 is on the line");
    }

    // ── UC-AP-06 — Any one of multiple buyers approves ───────────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_AP_06_any_one_buyer_approves()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // ASN spans PO-A (buyer = Buyer) and PO-B (buyer = SuperAdmin). Either buyer may approve (Phase 1).
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var poA = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, orderQty: 10m);
        var poB = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, orderQty: 20m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, poA.PoId, SecurityTestHarness.BuyerUserId);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, poB.PoId, SecurityTestHarness.SuperAdminUserId);
        var schedA = await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplierClient, poA.PoId);
        var schedB = await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplierClient, poB.PoId);

        var create = await supplierClient.PostAsJsonAsync("/api/asns/from-schedule", new CreateAsnFromScheduleRequest(
            ScheduleIds: new[] { schedA[poA.PoLineId], schedB[poB.PoLineId] },
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1)));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;
        await SendOkAsync(supplierClient, asnId);

        // Buyer X (the PO-A buyer) approves alone — buyer Y's separate action is not required (UC-AP-06).
        var buyerX = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyerX.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));
        (await Read<AsnDetailDto>(approve)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
    }

    // ════════════════════════════ C2 — buyer-facing approval queue ════════════════════════════

    // ── C2 — the approval queue shows ALL PendingApproval ASNs to ANY approver (any Asn.Approve holder), mirroring
    //    the PO-negotiation reviewer queue. Per-buyer routing was removed (nothing populates PurchaseOrder
    //    .BuyerUserId, so a buyer queue was always empty). Both the (non-admin) Buyer and an Admin see both ASNs. ──
    [SkippableFact]
    public async Task Pending_approvals_queue_shows_all_pending_to_any_approver()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Two independent ASNs, both sent for approval → PendingApproval.
        var (asn1, _, supplier1) = await NewDraftWithBuyerAsync(orderQty: 10m);
        await SendOkAsync(supplier1, asn1);
        var (asn2, _, supplier2) = await NewDraftWithBuyerAsync(orderQty: 10m);
        await SendOkAsync(supplier2, asn2);

        // Buyer (a non-admin Asn.Approve principal) sees BOTH pending ASNs (reviewer-sees-all).
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var buyerResp = await buyer.GetAsync("/api/asns/pending-approvals");
        buyerResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(buyerResp));
        var mine = (await Read<List<AsnApprovalListItemDto>>(buyerResp)).Data!;
        var row = mine.SingleOrDefault(r => r.AsnId == asn1);
        row.Should().NotBeNull(because: "a PendingApproval ASN appears for any approver");
        row!.SubmittedBy.Should().Be(SecurityTestHarness.Users.Supplier);
        row.SubmittedOn.Should().NotBeNull();
        row.PoCount.Should().Be(1);
        mine.Should().Contain(r => r.AsnId == asn2, because: "the approver sees ALL pending ASNs, not a buyer-routed subset");

        // An Admin also sees both.
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var adminResp = await admin.GetAsync("/api/asns/pending-approvals");
        adminResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(adminResp));
        var all = (await Read<List<AsnApprovalListItemDto>>(adminResp)).Data!;
        all.Should().Contain(r => r.AsnId == asn1);
        all.Should().Contain(r => r.AsnId == asn2);
    }

    // ── Authorization — a non-buyer (the supplier) cannot approve ────────────────────────────────────────
    [SkippableFact]
    public async Task Approve_by_non_buyer_is_forbidden()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplierClient) = await NewDraftWithBuyerAsync(orderQty: 10m);
        await SendOkAsync(supplierClient, asnId);

        // The supplier has Asn.Write but NOT Asn.Approve → the policy denies (403/Forbidden).
        var approve = await supplierClient.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().BeOneOf(new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized },
            because: "the supplier role does not hold Asn.Approve");
    }

    // ════════════════════════════ helpers ════════════════════════════

    private async Task<(Guid AsnId, ProcureToPayFlow.Setup Setup, HttpClient SupplierClient)> NewDraftWithBuyerAsync(
        decimal orderQty = 10m, Guid? buyerUserId = null)
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: orderQty);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId, buyerUserId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var create = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;
        return (asnId, setup, client);
    }

    private static async Task SendOkAsync(HttpClient supplierClient, Guid asnId)
    {
        var send = await supplierClient.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await send.Content.ReadAsStringAsync());
    }

    private async Task ConsumeBalanceAsync(Guid poLineId, decimal qty)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.PurchaseOrderLines.IgnoreQueryFilters().FirstAsync(l => l.Id == poLineId);
        line.ShippedQtyToDate += qty;
        await db.SaveChangesAsync();
    }

    private async Task<decimal> ShippedToDate(Guid poLineId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == poLineId)
            .Select(l => l.ShippedQtyToDate).FirstAsync();
    }

    private async Task AssertStatus(Guid asnId, AsnStatus expected)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var status = await db.Asns.IgnoreQueryFilters().Where(a => a.Id == asnId).Select(a => a.AsnStatus).FirstAsync();
        status.Should().Be(expected);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
