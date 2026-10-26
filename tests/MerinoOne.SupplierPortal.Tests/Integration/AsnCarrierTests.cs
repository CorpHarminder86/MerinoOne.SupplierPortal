using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Shipments.AsnListItemDto>;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R17 (F1 AC6-AC8, D3/D4/D7/D9) — the carrier a supplier picks on an ASN.
///
/// <para>The ASN stores a SNAPSHOT of the pick — <c>Carrier.Code</c> in <c>CarrierCode</c> and the description in
/// <c>CarrierName</c> — so reads never join the master. An unknown or inactive code is a 400; a blank code keeps the pre-R17
/// free-text <c>CarrierName</c> untouched. The carrier is mandatory only at Post. LN's <c>carrierName</c> wire key now carries
/// the CODE (user decision), falling back to the legacy name only for an ASN that never picked one.</para>
///
/// <para>The test DB is persistent, so every test mints its own carrier codes (<c>A</c> + seven hex digits = 8 characters,
/// within the 9-wide column) under the fixture company and removes them in <see cref="DisposeAsync"/>. The ASNs it creates
/// hold only the snapshot (no FK to the master), so deleting the carriers leaves them intact. No shared or seeded row is
/// mutated.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnCarrierTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    private readonly List<string> _codes = new();
    public AsnCarrierTests(IntegrationTestFixture fx) => _fx = fx;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!_fx.DbAvailable || _codes.Count == 0) return;
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var codes = _codes.ToList();
        await db.Carriers.IgnoreQueryFilters()
            .Where(c => c.TenantEntityId == IntegrationTestFixture.CompanyId && codes.Contains(c.Code))
            .ExecuteDeleteAsync();
    }

    // ═══════════════════════════════════ Create — resolve + snapshot ═══════════════════════════════════

    [SkippableFact]
    public async Task Create_with_a_valid_carrier_code_snapshots_the_code_and_the_description()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Blue Dart Express");
        var (setup, supplier) = await NewPoAsync();

        // The free-text name the caller also sends must NOT win over the master's description.
        var detail = await CreateOkAsync(supplier, NewAsn(setup, code, legacyName: "typed by hand"));

        detail.CarrierCode.Should().Be(code);
        detail.CarrierName.Should().Be("Blue Dart Express", because: "the description is snapshotted at write time");

        var row = await LoadAsnAsync(detail.Id);
        (row.CarrierCode, row.CarrierName).Should().Be((code, "Blue Dart Express"));

        // Snapshot-on-write: a later rename of the master row must not rewrite the ASN, and the read must not join it.
        await RenameCarrierAsync(code, "Renamed By LN");
        var reread = (await Read<AsnDetailDto>(await supplier.GetAsync($"/api/asns/{detail.Id}"))).Data!;
        (reread.CarrierCode, reread.CarrierName).Should().Be((code, "Blue Dart Express"));
    }

    [SkippableFact]
    public async Task A_carrier_with_no_description_snapshots_its_code_as_the_name()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync(description: null);   // LN can send a carrier code with no name
        var (setup, supplier) = await NewPoAsync();

        var detail = await CreateOkAsync(supplier, NewAsn(setup, code, legacyName: null));

        detail.CarrierCode.Should().Be(code);
        detail.CarrierName.Should().Be(code, because: "the code stands in as its own label when the master has no description");
    }

    [SkippableFact]
    public async Task Carrier_code_is_matched_case_insensitively_and_trimmed_and_stored_as_the_master_spells_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Mixed Case Carrier");   // "A" + lower-case hex: lower-casing it changes it
        var (setup, supplier) = await NewPoAsync();

        var detail = await CreateOkAsync(supplier, NewAsn(setup, $" {code.ToLowerInvariant()}"));

        detail.CarrierCode.Should().Be(code, because: "the snapshot carries the master's own spelling, not the caller's");
        detail.CarrierName.Should().Be("Mixed Case Carrier");
    }

    [SkippableFact]
    public async Task Unknown_carrier_code_is_rejected_with_400_and_nothing_is_created()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var unknown = $"NOPE{Guid.NewGuid():N}"[..8];
        var (setup, supplier) = await NewPoAsync();

        var resp = await supplier.PostAsJsonAsync("/api/asns", NewAsn(setup, unknown));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        var body = await Body(resp);
        body.Should().Contain("Unknown or inactive carrier").And.Contain(unknown);
        (await AsnCountForPoAsync(setup.PoId)).Should().Be(0, because: "a rejected carrier must persist nothing");
    }

    [SkippableFact]
    public async Task Inactive_carrier_code_is_rejected_with_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Retired Carrier", isActive: false);
        var (setup, supplier) = await NewPoAsync();

        var resp = await supplier.PostAsJsonAsync("/api/asns", NewAsn(setup, code));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        (await Body(resp)).Should().Contain("Unknown or inactive carrier").And.Contain(code);
        (await AsnCountForPoAsync(setup.PoId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task A_carrier_code_longer_than_the_nine_character_column_is_a_400_not_a_500()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();

        var resp = await supplier.PostAsJsonAsync("/api/asns", NewAsn(setup, "TENCHARS01"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_carrier_code_keeps_the_legacy_free_text_name_and_leaves_the_code_null(string? blank)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();

        var detail = await CreateOkAsync(supplier, NewAsn(setup, blank, legacyName: "Free Text Carrier"));

        detail.CarrierCode.Should().BeNull();
        detail.CarrierName.Should().Be("Free Text Carrier", because: "pre-R17 behaviour is unchanged when no master pick is made");
    }

    [SkippableFact]
    public async Task From_schedule_create_resolves_and_snapshots_the_carrier_too()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Schedule Carrier");
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, confirm: false);   // Released: /accept materialises the schedules
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var schedules = await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplier, setup.PoId);
        var scheduleIds = new[] { schedules[setup.PoLineId] };

        var unknown = $"NOPE{Guid.NewGuid():N}"[..8];
        var rejected = await supplier.PostAsJsonAsync("/api/asns/from-schedule",
            new CreateAsnFromScheduleRequest(scheduleIds, DateTime.UtcNow.Date.AddDays(1), CarrierCode: unknown));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(rejected));
        (await AsnCountForPoAsync(setup.PoId)).Should().Be(0);

        var resp = await supplier.PostAsJsonAsync("/api/asns/from-schedule",
            new CreateAsnFromScheduleRequest(scheduleIds, DateTime.UtcNow.Date.AddDays(1), CarrierCode: code));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var detail = (await Read<AsnDetailDto>(resp)).Data!;

        detail.CarrierCode.Should().Be(code);
        detail.CarrierName.Should().Be("Schedule Carrier");
    }

    // ═══════════════════════════════════ Update — full overwrite ═══════════════════════════════════

    [SkippableFact]
    public async Task Update_switches_the_carrier_and_refreshes_the_snapshot()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var first = await SeedCarrierAsync("First Carrier");
        var second = await SeedCarrierAsync("Second Carrier");
        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, first));

        var put = await supplier.PutAsJsonAsync($"/api/asns/{created.Id}", UpdateReq(setup, second));
        put.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(put));
        var updated = (await Read<AsnDetailDto>(put)).Data!;

        updated.CarrierCode.Should().Be(second);
        updated.CarrierName.Should().Be("Second Carrier", because: "the snapshot is re-taken on every write");
    }

    [SkippableFact]
    public async Task Update_with_an_unknown_carrier_is_a_400_and_leaves_the_asn_on_its_previous_carrier()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Keeper Carrier");
        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, code));

        var put = await supplier.PutAsJsonAsync($"/api/asns/{created.Id}", UpdateReq(setup, $"NOPE{Guid.NewGuid():N}"[..8]));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(put));
        var row = await LoadAsnAsync(created.Id);
        (row.CarrierCode, row.CarrierName).Should().Be((code, "Keeper Carrier"));
    }

    [SkippableFact]
    public async Task Update_with_a_blank_carrier_clears_the_code_and_keeps_the_supplied_free_text()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Picked Carrier");
        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, code));

        var put = await supplier.PutAsJsonAsync($"/api/asns/{created.Id}", UpdateReq(setup, carrierCode: null, legacyName: "Back To Free Text"));
        put.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(put));
        var updated = (await Read<AsnDetailDto>(put)).Data!;

        updated.CarrierCode.Should().BeNull(because: "Update is full-overwrite, like the shipment references");
        updated.CarrierName.Should().Be("Back To Free Text");
    }

    // ═══════════════════════════════════ Post — carrier is mandatory ═══════════════════════════════════

    [SkippableFact]
    public async Task Post_without_a_carrier_is_blocked_then_succeeds_once_one_is_picked()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Posting Carrier");
        var (setup, supplier) = await NewPoAsync();

        // Every shipment ref is supplied; ONLY the carrier is left blank. Deliberately not EnsureShipmentRefsAsync —
        // that helper would fill the carrier too.
        var created = await CreateOkAsync(supplier, NewAsn(setup, carrierCode: null, legacyName: "Free Text Carrier") with
        {
            InvoiceNo = "INV-C1", BillOfLading = "BOL-C1", PackingList = "PL-C1",
        });
        await SendAndConfirmAsync(supplier, created.Id);

        var blocked = await ProcureToPayFlow.PostAsync(supplier, created.Id);
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(blocked));
        (await Body(blocked)).Should().Contain("Fill in carrier on the ASN before posting it.");
        (await LoadAsnAsync(created.Id)).AsnStatus.Should().Be(AsnStatus.Approved,
            because: "a blocked post leaves the ASN confirmed, not submitted");

        // The supplier picks a carrier (an Approved ASN stays editable; lines + quantities are unchanged), then posts.
        var put = await supplier.PutAsJsonAsync($"/api/asns/{created.Id}",
            UpdateReq(setup, code) with { InvoiceNo = "INV-C1", BillOfLading = "BOL-C1", PackingList = "PL-C1" });
        put.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(put));

        var posted = await ProcureToPayFlow.PostAsync(supplier, created.Id);
        posted.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(posted));
        var detail = (await Read<AsnDetailDto>(posted)).Data!;
        detail.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
        (detail.CarrierCode, detail.CarrierName).Should().Be((code, "Posting Carrier"));
    }

    [SkippableFact]
    public async Task Post_names_the_carrier_first_when_the_references_are_missing_too()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, carrierCode: null));   // no carrier, no refs
        await SendAndConfirmAsync(supplier, created.Id);

        var blocked = await ProcureToPayFlow.PostAsync(supplier, created.Id);

        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(blocked));
        (await Body(blocked)).Should().Contain("Fill in carrier, invoice no., bill of lading, packing list on the ASN before posting it.");
    }

    // ═══════════════════════════════════ Outbound — carrierName carries the CODE ═══════════════════════════════════

    [SkippableFact]
    public async Task Ln_input_document_and_legacy_payload_carry_the_carrier_code_as_carrierName()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Outbound Carrier");
        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, code, legacyName: "legacy text"));

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The Dynamic route (the live AsnPost row): the input document the JSONata request expression projects from.
        var inputJson = await new AsnInputDocumentBuilder().BuildJsonAsync(db, created.Id, "AsnPost", null);
        inputJson.Should().NotBeNull();
        using (var doc = JsonDocument.Parse(inputJson!))
            doc.RootElement.GetProperty("carrierName").GetString().Should().Be(code,
                because: "LN's ASNDetail.carrierName key carries the carrier CODE since R17 — not the description");

        // The Legacy/Mock builder is kept in lockstep.
        var legacyJson = await AsnOutboundPayloadBuilder.BuildJsonAsync(db, created.Id);
        legacyJson.Should().NotBeNull();
        using (var doc = JsonDocument.Parse(legacyJson!))
            doc.RootElement.GetProperty("CarrierName").GetString().Should().Be(code);
    }

    [SkippableFact]
    public async Task Ln_input_document_falls_back_to_the_legacy_name_for_an_asn_with_no_carrier_code()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, carrierCode: null, legacyName: "Pre R17 Carrier"));

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var inputJson = await new AsnInputDocumentBuilder().BuildJsonAsync(db, created.Id, "AsnPost", null);
        using (var doc = JsonDocument.Parse(inputJson!))
            doc.RootElement.GetProperty("carrierName").GetString().Should().Be("Pre R17 Carrier");

        var legacyJson = await AsnOutboundPayloadBuilder.BuildJsonAsync(db, created.Id);
        using (var doc = JsonDocument.Parse(legacyJson!))
            doc.RootElement.GetProperty("CarrierName").GetString().Should().Be("Pre R17 Carrier");
    }

    // ═══════════════════════════════════ Read side — receipt facts + shipment status ═══════════════════════════════════

    [SkippableFact]
    public async Task Detail_and_list_expose_the_lns_shipment_status_carrier_code_and_line_receipt_facts()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = await SeedCarrierAsync("Receipt Carrier");
        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, code));

        // Before LN reports anything the new fields are null (not zero / not min-date).
        created.ErpShipmentStatus.Should().BeNull();
        created.Lines.Should().ContainSingle().Which.ReceivedQty.Should().BeNull();

        // What /inbound/asn-update would have written.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var asn = await db.Asns.IgnoreQueryFilters().Include(a => a.Lines).FirstAsync(a => a.Id == created.Id);
            asn.ErpShipmentStatus = "Received";
            var line = asn.Lines.Single(l => !l.IsDeleted);
            line.ReceivedQty = 7.5m;
            line.ActualDeliveryDate = new DateTime(2026, 10, 20);
            await db.SaveChangesAsync();
        }

        var detail = (await Read<AsnDetailDto>(await supplier.GetAsync($"/api/asns/{created.Id}"))).Data!;
        detail.ErpShipmentStatus.Should().Be("Received");
        detail.CarrierCode.Should().Be(code);
        var detailLine = detail.Lines.Should().ContainSingle().Subject;
        detailLine.ReceivedQty.Should().Be(7.5m);
        detailLine.ActualDeliveryDate.Should().NotBeNull();
        detailLine.ActualDeliveryDate!.Value.Date.Should().Be(new DateTime(2026, 10, 20));

        // The list row carries the same two header facts.
        var listResp = await supplier.GetAsync($"/api/asns?search={Uri.EscapeDataString(detail.AsnNumber)}");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(listResp));
        var item = (await Read<ContractsPagedResult>(listResp)).Data!.Items.Should().ContainSingle().Subject;
        (item.CarrierCode, item.ErpShipmentStatus).Should().Be((code, "Received"));
    }

    // ───────────────────────────────────────── helpers ─────────────────────────────────────────

    private static string NewCode() => $"A{Guid.NewGuid():N}"[..8];

    /// <summary>Inserts one Carrier into the fixture company and tracks it for removal in <see cref="DisposeAsync"/>.</summary>
    private async Task<string> SeedCarrierAsync(string? description, bool isActive = true)
    {
        var code = NewCode();
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Carriers.Add(new Carrier
        {
            Id = Guid.NewGuid(),
            TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId,
            Code = code,
            Description = description,
            IsActive = isActive,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        _codes.Add(code);
        return code;
    }

    private async Task RenameCarrierAsync(string code, string description)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Carriers.IgnoreQueryFilters()
            .Where(c => c.TenantEntityId == IntegrationTestFixture.CompanyId && c.Code == code)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Description, description));
    }

    private async Task<(ProcureToPayFlow.Setup Setup, HttpClient Supplier)> NewPoAsync(decimal orderQty = 10m)
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: orderQty);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        return (setup, supplier);
    }

    /// <summary>The standard single-line ASN with the carrier fields set explicitly (never SimpleAsn's default pick).</summary>
    private static CreateAsnRequest NewAsn(ProcureToPayFlow.Setup setup, string? carrierCode, string? legacyName = "Legacy Carrier")
        => ProcureToPayFlow.SimpleAsn(setup) with { CarrierCode = carrierCode, CarrierName = legacyName };

    /// <summary>A same-lines/same-quantity update, so it is also legal on an Approved ASN (lines + quantities are frozen there).</summary>
    private static UpdateAsnRequest UpdateReq(ProcureToPayFlow.Setup setup, string? carrierCode, string? legacyName = null)
        => new(
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
            TimeWindow: null, CarrierName: legacyName, TrackingNumber: "TRK", VehicleNumber: null, DriverName: null,
            DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest> { new(setup.PoLineId, ShippedQty: setup.OrderQty, BatchNumber: null, ExpiryDate: null) },
            CarrierCode: carrierCode);

    private async Task<AsnDetailDto> CreateOkAsync(HttpClient supplier, CreateAsnRequest req)
    {
        var resp = await supplier.PostAsJsonAsync("/api/asns", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<AsnDetailDto>(resp)).Data!;
    }

    private async Task SendAndConfirmAsync(HttpClient supplier, Guid asnId)
    {
        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));

        var buyer = await SecurityTestHarness.ClientAsAsync(_fx, SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));
    }

    private async Task<Asn> LoadAsnAsync(Guid asnId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Asns.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == asnId);
    }

    private async Task<int> AsnCountForPoAsync(Guid poId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Asns.IgnoreQueryFilters()
            .CountAsync(a => !a.IsDeleted
                             && (a.PurchaseOrderId == poId || a.PurchaseOrders.Any(j => j.PurchaseOrderId == poId)));
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
