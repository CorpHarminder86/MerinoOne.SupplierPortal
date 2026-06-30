using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Outbound payload → SyncLog through the REAL host with <c>Integration:Mode=Mock</c>. The kept-running
/// <c>OutboxDispatcherWorker</c> drains rows the handlers enqueue and writes an OUTBOUND
/// <see cref="MerinoOne.SupplierPortal.Domain.Entities.Integration.InforSyncLog"/> carrying the canonical
/// "what we sent" body (built by the SAME builder Live uses), so the payload viewer (HasPayload=true) can render it:
/// <list type="bullet">
///   <item><b>(a) ASN submit</b> → an outbound <c>Asn</c> SyncLog with a non-null payload (HasPayload=true).</item>
///   <item><b>(b) supplier change request approve</b> → an outbound <c>SupplierChange</c> SyncLog with a payload.</item>
///   <item><b>(c) supplier sync</b> → there is no HTTP/command trigger that enqueues a standalone SupplierSync, so
///         the dispatcher → SyncLog path is not directly reachable; we instead assert the Mock builds the canonical
///         <c>Supplier</c> payload (the body the dispatcher would log) via the reachable service seam, and note the
///         missing trigger.</item>
/// </list>
/// (Invoice outbound payload is already covered by the GRN auto-post chain test.) Every test uses a fresh tagged
/// supplier; money path runs with the scope gate OFF.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class OutboundPayloadSyncLogTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public OutboundPayloadSyncLogTests(IntegrationTestFixture fx) => _fx = fx;

    // -------------------- (a) ASN submit → outbound Asn SyncLog with payload --------------------

    [SkippableFact]
    public async Task Asn_submit_dispatches_an_outbound_asn_synclog_with_payload()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asn = (await Read<AsnDetailDto>(createResp)).Data!;

        // R5 — submit through Send-for-Approval → buyer Approve (the AsnPost outbox is enqueued at the submit step).
        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asn.Id);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        (await Read<AsnDetailDto>(submitResp)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));

        // The AsnPost outbox row was enqueued synchronously inside the submit transaction.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var enqueued = await db.OutboxMessages.IgnoreQueryFilters()
                .AnyAsync(m => !m.IsDeleted && m.EntityId == asn.Id
                               && m.TransactionType == OutboxTransactionType.AsnPost);
            enqueued.Should().BeTrue(because: "ASN submit enqueues an AsnPost outbox row");
        }

        // The dispatcher (Mock) drains it and writes an OUTBOUND Asn SyncLog WITH a payload. Poll up to ~15s.
        var log = await PollForLogAsync(OutboxEntity.Asn, asn.Id.ToString(), requirePayload: true, TimeSpan.FromSeconds(15));
        log.Should().NotBeNull(because: "the dispatcher logs the AsnPost outbound with the canonical payload");
        log!.PayloadJson.Should().NotBeNullOrWhiteSpace(because: "HasPayload=true — the ASN payload viewer JSON is present");

        // Cross-check through the API (Admin holds Integration.Read): the row surfaces with HasPayload=true.
        var dto = await FindSyncLogViaApiAsync(OutboxEntity.Asn, log.Id);
        dto.Should().NotBeNull(because: "the outbound Asn SyncLog is visible to a tenant-A admin via the API");
        dto!.HasPayload.Should().BeTrue(because: "the payload viewer flag mirrors PayloadJson != null");
        dto.Direction.Should().Be(nameof(SyncDirection.Outbound));
    }

    // -------------------- (b) supplier change approve → outbound SupplierChange SyncLog with payload --------------------

    [SkippableFact]
    public async Task Supplier_change_approve_dispatches_an_outbound_supplierchange_synclog_with_payload()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = new CreateSupplierChangeRequestRequest(
            SupplierId: supplier.SupplierId,
            Summary: $"CR-outbound {tag}",
            Lines: new List<SupplierChangeLineInput>
            {
                new(TargetEntity: "Supplier", Operation: "Edit", FieldName: "Website",
                    NewValue: $"https://outbound-{tag}.example.test"),
            });

        var createResp = await supplierClient.PostAsJsonAsync("/api/suppliers/change-requests", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var crId = (await Read<SupplierChangeRequestDto>(createResp)).Data!.Id;

        var submitResp = await supplierClient.PostAsync($"/api/suppliers/change-requests/{crId}/submit", null);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));

        var adminClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var approveResp = await adminClient.PostAsync($"/api/suppliers/change-requests/{crId}/approve", null);
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approveResp));

        // Approve enqueues a SupplierChange outbox row (EntityId = changeRequestId).
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var enqueued = await db.OutboxMessages.IgnoreQueryFilters()
                .AnyAsync(m => !m.IsDeleted && m.EntityId == crId
                               && m.TransactionType == OutboxTransactionType.SupplierChange);
            enqueued.Should().BeTrue(because: "approving a change request enqueues a SupplierChange outbox row");
        }

        // The dispatcher (Mock) drains it → OUTBOUND SupplierChange SyncLog WITH the canonical end-state payload.
        var log = await PollForLogAsync(OutboxEntity.SupplierChange, crId.ToString(), requirePayload: true, TimeSpan.FromSeconds(15));
        log.Should().NotBeNull(because: "the dispatcher logs the SupplierChange outbound with the canonical payload");
        log!.PayloadJson.Should().NotBeNullOrWhiteSpace(because: "the SupplierChange payload viewer JSON is present");
    }

    // -------------------- (c) supplier sync — no direct trigger; assert the Mock builds the Supplier payload --------------------

    [SkippableFact]
    public async Task Supplier_sync_builds_the_canonical_supplier_payload_no_direct_trigger()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // NOTE (reason): there is no HTTP endpoint / command that enqueues a standalone SupplierSync outbox row
        // (SupplierSync/OutboxEntity.Supplier is only reached via the dispatcher route + RetryIntegrationError), so
        // the handler→outbox→dispatcher→SyncLog path is not directly reachable from the API. We assert the reachable
        // seam instead: the Mock builds the SAME canonical Supplier payload the dispatcher would log on a sync.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);

        using var scope = _fx.Factory.Services.CreateScope();
        var infor = scope.ServiceProvider.GetRequiredService<IInforIntegrationService>();

        var result = await infor.SyncSupplierAsync(supplier.SupplierId, CancellationToken.None);

        result.Success.Should().BeTrue(because: "the Mock supplier sync always succeeds");
        result.RequestPayloadJson.Should().NotBeNullOrWhiteSpace(
            because: "the Mock builds the canonical Supplier outbound payload (the body the dispatcher would log)");
        result.RequestPayloadJson!.Should().Contain(supplier.SupplierCode,
            because: "the Supplier payload carries the supplier's code (field-map confirmation body)");
    }

    // ====================================================================================================
    // helpers
    // ====================================================================================================

    /// <summary>Polls the DB for an outbound SyncLog of (entityName, entityId), optionally requiring a payload.</summary>
    private async Task<MerinoOne.SupplierPortal.Domain.Entities.Integration.InforSyncLog?> PollForLogAsync(
        string entityName, string entityId, bool requirePayload, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var log = await db.InforSyncLogs.IgnoreQueryFilters()
                    .Where(l => l.TenantId == IntegrationTestFixture.TenantId
                                && l.EntityName == entityName
                                && l.EntityId == entityId
                                && l.Direction == SyncDirection.Outbound
                                && l.Status == SyncStatus.Success
                                && (!requirePayload || l.PayloadJson != null))
                    .OrderByDescending(l => l.SyncedAt)
                    .FirstOrDefaultAsync();
                if (log is not null) return log;
            }
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(500);
        }
    }

    /// <summary>Reads the outbound SyncLog via the Admin (Integration.Read) API and returns the matching DTO.</summary>
    private async Task<MerinoOne.SupplierPortal.Contracts.Integration.InforSyncLogDto?> FindSyncLogViaApiAsync(
        string entityName, Guid id)
    {
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var resp = await admin.GetAsync($"/api/integration/sync-log?page=1&pageSize=500&entityName={entityName}&direction=Outbound");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var paged = await Read<PagedResult<MerinoOne.SupplierPortal.Contracts.Integration.InforSyncLogDto>>(resp);
        return paged.Data!.Items.FirstOrDefault(x => x.Id == id);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
