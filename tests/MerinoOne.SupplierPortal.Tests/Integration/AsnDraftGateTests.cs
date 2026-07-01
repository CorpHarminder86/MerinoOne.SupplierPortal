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
/// R4 §6.2 draft-scope gate: a Draft ASN's Save Changes (UpdateAsn) and Send-For-Approval are hard-blocked when
/// a covered PO is not shippable (e.g. reset to Released by an ERP Modify) or another ASN for the SAME PO is
/// already pending buyer approval. Complements PoConfirmationGateTests (create + approve-time gate).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnDraftGateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IntegrationTestFixture _fx;
    public AsnDraftGateTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task SaveChanges_OnDraft_Blocked_WhenPoResetToReleased()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: true);   // Accepted → ship-gate open
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        // ERP Modify re-releases the PO mid-fulfilment (UC-PO-06).
        await SetPoStatusAsync(setup.PoId, PoStatus.Released);

        var upd = new UpdateAsnRequest(
            DateTime.UtcNow.Date.AddDays(2), null, "Carrier", "TRK2", null, null, null, "edit",
            new List<CreateAsnLineRequest> { new(setup.PoLineId, setup.OrderQty, null, null) });
        var save = await supplier.PutAsJsonAsync($"/api/asns/{asnId}", upd);

        save.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(save));
        (await Read<AsnDetailDto>(save)).Errors.Should().Contain(e => e.Contains("Accept"),
            because: "saving a Draft on a re-Released PO must be blocked (§6.2)");
    }

    [SkippableFact]
    public async Task SendForApproval_OnDraft_Blocked_WhenPoResetToReleased()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: true);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        await SetPoStatusAsync(setup.PoId, PoStatus.Released);

        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(send));
        (await Read<AsnDetailDto>(send)).Errors.Should().Contain(e => e.Contains("Accept"),
            because: "sending a Draft for approval on a re-Released PO must be blocked (§6.2)");
    }

    [SkippableFact]
    public async Task SendForApproval_Blocked_WhenAnotherAsnForSamePo_IsPending()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: true);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // ASN-A → PendingApproval (partial qty so a second ASN is possible).
        var createA = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 4m));
        var asnA = (await Read<AsnDetailDto>(createA)).Data!.Id;
        var sendA = await supplier.PostAsJsonAsync($"/api/asns/{asnA}/send-for-approval", new SendForApprovalRequest());
        sendA.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(sendA));
        await AssertAsnStatus(asnA, AsnStatus.PendingApproval);

        // ASN-B draft (create is NOT gated on pending) — but Send-For-Approval IS: same PO has a pending ASN.
        var createB = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 4m));
        createB.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createB));
        var asnB = (await Read<AsnDetailDto>(createB)).Data!.Id;

        var sendB = await supplier.PostAsJsonAsync($"/api/asns/{asnB}/send-for-approval", new SendForApprovalRequest());
        sendB.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(sendB));
        (await Read<AsnDetailDto>(sendB)).Errors.Should().Contain(e => e.Contains("pending buyer approval"),
            because: "only one ASN per PO may be pending buyer approval at a time");
    }

    [SkippableFact]
    public async Task PendingApproval_ASN_is_gate_blocked_for_the_buyer_when_Po_reReleased()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: true);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;
        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));

        // While the PO is shippable the PendingApproval ASN is NOT gate-blocked (buyer may approve/reject).
        var before = (await Read<AsnDetailDto>(await supplier.GetAsync($"/api/asns/{asnId}"))).Data!;
        before.AsnStatus.Should().Be("PendingApproval");
        before.ShipBlocked.Should().BeFalse();

        // ERP Modify re-releases the PO — now the buyer's Approve/Reject must be gate-blocked.
        await SetPoStatusAsync(setup.PoId, PoStatus.Released);

        var after = (await Read<AsnDetailDto>(await supplier.GetAsync($"/api/asns/{asnId}"))).Data!;
        after.ShipBlocked.Should().BeTrue(because: "a PendingApproval ASN on a re-Released PO is gate-blocked for the buyer");
        after.ShipBlockReason.Should().Contain("Accept");

        // And the buyer's Approve is hard-blocked server-side (belt-and-braces behind the disabled button).
        var buyer = await SecurityTestHarness.ClientAsAsync(_fx, SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        (await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest())).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, because: "Approve→Submit re-checks the confirmation gate");
    }

    // ── helpers ──
    private async Task SetPoStatusAsync(Guid poId, PoStatus status)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == poId);
        po.PoStatus = status;
        await db.SaveChangesAsync();
    }

    private async Task AssertAsnStatus(Guid asnId, AsnStatus expected)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Asns.IgnoreQueryFilters().Where(a => a.Id == asnId).Select(a => a.AsnStatus).FirstAsync())
            .Should().Be(expected);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
