using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R4 — TSD R4 Addendum §6 / UC-PO-01..03,05,08..10; UC-ASN-07..09; DI-05. End-to-end PO confirmation-gate
/// behaviour through the REAL host: a new PO lands Released and BLOCKS ASN creation; acknowledge unblocks only
/// for Ack/Auto modes; accept stamps acceptedAt and unblocks every mode; reject needs a reason and terminally
/// blocks; an ERP cancel blocks; and the audited admin override (and its no-reason rejection) is exercised.
///
/// <para>Runs with the scope gate OFF (money path). Each test owns a fresh tagged supplier + PO via the inbound
/// push; PO status is driven through the real supplier endpoints (/acknowledge, /accept, /reject) so the whole
/// command chain is exercised. ASN create/submit go through the supplier client; the override path uses a
/// SuperAdmin principal (the only seeded role holding BOTH Asn.Write AND PurchaseOrder.OverrideGate).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PoConfirmationGateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public PoConfirmationGateTests(IntegrationTestFixture fx) => _fx = fx;

    // ── UC-PO-01 — NewPo_LandsReleased_BlocksAsnCreation ────────────────────────────────────────────────
    [SkippableFact]
    public async Task NewPo_LandsReleased_BlocksAsnCreation()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // A fresh PO ingested as Released (AcceptToShip default) — NOT confirmed.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        await AssertPoStatus(setup.PoId, PoStatus.Released);

        // An attempt to create an ASN for this PO is BLOCKED (the message names the required Accept action).
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        (await Read<AsnDetailDto>(resp)).Errors.Should().Contain(e => e.Contains("Accept"));
    }

    // ── UC-PO-02 — Acknowledge_UnblocksOnlyForAckOrAutoModes ────────────────────────────────────────────
    [SkippableFact]
    public async Task Acknowledge_UnblocksOnlyForAckOrAutoModes()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Supplier A — AcknowledgeToShip: acknowledge OPENS the gate. Supplier B — AcceptToShip: acknowledge does NOT.
        var ack = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        await SetConfirmationModeAsync(ack.Supplier.SupplierId, PoConfirmationMode.AcknowledgeToShip);
        var accept = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);   // default AcceptToShip

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Acknowledge both POs through the real endpoint.
        (await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{ack.PoId}/acknowledge", new AcknowledgePoRequest()))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{accept.PoId}/acknowledge", new AcknowledgePoRequest()))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertPoStatus(ack.PoId, PoStatus.Acknowledged);
        await AssertPoStatus(accept.PoId, PoStatus.Acknowledged);

        // AcknowledgeToShip supplier → ASN creation ALLOWED at Acknowledged.
        var ackResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(ack));
        ackResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(ackResp));

        // AcceptToShip supplier → STILL blocked at Acknowledged (Accept required).
        var acceptResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(accept));
        acceptResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(acceptResp));
        (await Read<AsnDetailDto>(acceptResp)).Errors.Should().Contain(e => e.Contains("Accept"));
    }

    // ── UC-PO-03 — Accept_StampsAcceptedAt_UnblocksAllModes ─────────────────────────────────────────────
    [SkippableFact]
    public async Task Accept_StampsAcceptedAt_UnblocksAllModes()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);   // AcceptToShip default
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var acceptResp = await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{setup.PoId}/accept", new AcceptPoRequest());
        acceptResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(acceptResp));

        // PoStatus == Accepted and acceptedAt is stamped.
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == setup.PoId);
            po.PoStatus.Should().Be(PoStatus.Accepted);
            po.AcceptedAt.Should().NotBeNull(because: "accept stamps acceptedAt (UC-PO-03)");
        }

        // ASN creation now allowed (Accepted opens the gate for every mode incl. the AcceptToShip default).
        var asnResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        asnResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(asnResp));
    }

    // ── UC-PO-05 — Reject_RequiresReason_BlocksTerminally ───────────────────────────────────────────────
    [SkippableFact]
    public async Task Reject_RequiresReason_BlocksTerminally()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Reject WITHOUT a reason → 400 (the reason is mandatory).
        var noReason = await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{setup.PoId}/reject", new { reason = "" });
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: "a rejection reason is mandatory (UC-PO-05)");

        // Reject WITH a reason → 200; reason persisted; PoStatus == Rejected.
        const string reason = "Cannot meet the requested delivery date.";
        var rejected = await supplierClient.PostAsJsonAsync($"/api/purchase-orders/{setup.PoId}/reject", new RejectPoRequest(reason));
        rejected.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(rejected));
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == setup.PoId);
            po.PoStatus.Should().Be(PoStatus.Rejected);
            po.RejectionReason.Should().Be(reason, because: "the reason is persisted (UC-PO-05)");
        }

        // ASN creation blocked terminally (Rejected → block in every mode).
        var asnResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        asnResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(asnResp));
    }

    // ── UC-PO-08 — ErpCancel_BlocksAllAsns ──────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task ErpCancel_BlocksAllAsns()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // PO confirmed (Accepted) first, then an ERP cancellation moves it to the terminal Cancelled state.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);   // confirm:true → Accepted
        await SetPoStatusAsync(setup.PoId, PoStatus.Cancelled);

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a Cancelled PO blocks all new/draft ASNs (UC-PO-08)");
    }

    // ── UC-PO-09 — AdminOverride_RequiresReason_IsAudited ───────────────────────────────────────────────
    [SkippableFact]
    public async Task AdminOverride_RequiresReason_IsAudited()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // A Released PO (gate BLOCKS). Grant the SuperAdmin principal write on this supplier's seccode so the
        // ASN-create write resolves; SuperAdmin holds BOTH Asn.Write and PurchaseOrder.OverrideGate.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        await _fx.GrantSecRightAsync(setup.Supplier.SeccodeId, SecurityTestHarness.Users.SuperAdmin, canWrite: true);
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.SuperAdmin, IntegrationTestFixture.CompanyId);

        // Override WITHOUT a reason → still blocked (an empty reason is "no override requested").
        var noReason = await CreateAsnWithOverrideAsync(admin, setup, overrideReason: "");
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "an override without a reason is rejected (UC-PO-09)");

        // Override WITH a reason → ASN is permitted exceptionally.
        const string reason = "Customer escalation — ship ahead of formal acceptance.";
        var ok = await CreateAsnWithOverrideAsync(admin, setup, overrideReason: reason);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(ok));

        // The override + reason is recorded on the PO's audit trail (PO-targeted "Gate override" row).
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditRow = await db.AuditEntries.IgnoreQueryFilters()
            .Where(a => a.EntityName == nameof(PurchaseOrder) && a.EntityId == setup.PoId
                        && a.FieldName!.StartsWith("Gate override"))
            .OrderByDescending(a => a.ChangedOn)
            .FirstOrDefaultAsync();
        auditRow.Should().NotBeNull(because: "the audited override row is written (UC-PO-09)");
        auditRow!.NewValue.Should().Contain(reason, because: "the audit carries the override reason");
        auditRow.ChangedBy.Should().Be(SecurityTestHarness.Users.SuperAdmin, because: "the audit names the actor");
    }

    // ── UC-PO-10 — AutoAccept_PermitsAsnAtReleased ──────────────────────────────────────────────────────
    // The AutoAccept ingest auto-stamp (Released→Accepted at push) is feature-flagged off in the test host, so
    // here we set the supplier to AutoAccept, leave the PO at Released, and assert the GATE permits ASN creation
    // at Released for that mode (the §6.2 matrix row that the unit test pins, exercised end-to-end).
    [SkippableFact]
    public async Task AutoAccept_PermitsAsnAtReleased()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        await SetConfirmationModeAsync(setup.Supplier.SupplierId, PoConfirmationMode.AutoAccept);
        await AssertPoStatus(setup.PoId, PoStatus.Released);

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "an AutoAccept supplier may ship immediately at Released — no manual confirmation step (UC-PO-10)");
    }

    // ── UC-ASN-07 — UnconfirmedPo_BlocksAsnCreation (the gate-from-the-ASN-side view) ───────────────────
    [SkippableFact]
    public async Task UnconfirmedPo_BlocksAsnCreation()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);   // Released, AcceptToShip
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var resp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        (await Read<AsnDetailDto>(resp)).Errors.Should().Contain(e => e.Contains("Accept"),
            because: "the block names the required Accept action (UC-ASN-07)");
    }

    // ── UC-ASN-08 — DraftAsn_ReBlocked_OnMaterialModify (gate re-evaluated at submit) ────────────────────
    [SkippableFact]
    public async Task DraftAsn_ReBlocked_OnMaterialModify()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Confirmed PO → save a Draft ASN (gate open at create).
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);   // Accepted
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        // Send for approval (the gate is NOT checked here — only at the buyer Approve/Submit step).
        var send = await supplierClient.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));

        // A mid-fulfilment ERP material modify resets the PO to Released after the ASN was sent for approval.
        await SetPoStatusAsync(setup.PoId, PoStatus.Released);

        // R5 — the buyer Approve runs the submit path; the gate is re-evaluated there and BLOCKS (UC-ASN-08).
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var submitResp = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(submitResp));
        (await Read<AsnDetailDto>(submitResp)).Errors.Should().Contain(e => e.Contains("Accept"));
        await AssertAsnStatus(asnId, AsnStatus.PendingApproval);   // submit rolled back; approval not flipped.
    }

    // ── UC-ASN-09 — InFlightAsn_UnaffectedByModify (already-Submitted is never re-blocked) ───────────────
    [SkippableFact]
    public async Task InFlightAsn_UnaffectedByModify()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);   // Accepted
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        // Submit (via Send-for-Approval → Approve) while the gate is open → Submitted.
        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        await AssertAsnStatus(asnId, AsnStatus.Submitted);

        // An ERP material modify resets the PO to Released AFTER submission. The submitted ASN is neither blocked
        // nor reversed — it stays Submitted (only NEW/Draft ASNs are gated, UC-ASN-09).
        await SetPoStatusAsync(setup.PoId, PoStatus.Released);
        var getResp = await supplierClient.GetAsync($"/api/asns/{asnId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read<AsnDetailDto>(getResp)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
    }

    // ── DI-05 — Gate_EnforcedAtCreateAndSubmit ──────────────────────────────────────────────────────────
    // Proves the gate is enforced at BOTH entry points: a PO reset to Released between create and submit lets the
    // Draft save (created while Accepted) but blocks the submit; AND a PO Released from the start blocks the create.
    [SkippableFact]
    public async Task Gate_EnforcedAtCreateAndSubmit()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // (a) CREATE is gated: a Released PO blocks the Draft save outright.
        var releasedFromStart = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);
        var createBlocked = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(releasedFromStart));
        createBlocked.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the gate is enforced at create (DI-05)");

        // (b) SUBMIT is gated: a Draft created while Accepted is blocked on the Approve→Submit step once the PO
        //     resets to Released. The gate runs in the submit executor (reached via buyer Approve).
        var confirmed = await ProcureToPayFlow.SeedPoAsync(_fx);   // Accepted
        await ProcureToPayFlow.AssignBuyerAsync(_fx, confirmed.PoId);
        var createOk = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(confirmed));
        createOk.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createOk));
        var asnId = (await Read<AsnDetailDto>(createOk)).Data!.Id;

        var sendOk = await supplierClient.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        sendOk.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(sendOk));
        await SetPoStatusAsync(confirmed.PoId, PoStatus.Released);
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var submitBlocked = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        submitBlocked.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the gate is also enforced at the Approve→Submit step (DI-05)");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    private static async Task<HttpResponseMessage> CreateAsnWithOverrideAsync(
        HttpClient client, ProcureToPayFlow.Setup s, string overrideReason)
    {
        var body = new
        {
            purchaseOrderId = s.PoId,
            expectedDeliveryDate = DateTime.UtcNow.Date.AddDays(1),
            lines = new[] { new { purchaseOrderLineId = s.PoLineId, shippedQty = s.OrderQty } },
            overrideReason,
        };
        return await client.PostAsJsonAsync("/api/asns", body);
    }

    private async Task SetConfirmationModeAsync(Guid supplierId, PoConfirmationMode mode)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var supplier = await db.Suppliers.IgnoreQueryFilters().FirstAsync(s => s.Id == supplierId);
        supplier.PoConfirmationMode = mode;
        await db.SaveChangesAsync();
    }

    private async Task SetPoStatusAsync(Guid poId, PoStatus status)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == poId);
        po.PoStatus = status;
        await db.SaveChangesAsync();
    }

    private async Task AssertPoStatus(Guid poId, PoStatus expected)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var status = await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == poId)
            .Select(p => p.PoStatus).FirstAsync();
        status.Should().Be(expected);
    }

    private async Task AssertAsnStatus(Guid asnId, AsnStatus expected)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var status = await db.Asns.IgnoreQueryFilters().Where(a => a.Id == asnId)
            .Select(a => a.AsnStatus).FirstAsync();
        status.Should().Be(expected);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
