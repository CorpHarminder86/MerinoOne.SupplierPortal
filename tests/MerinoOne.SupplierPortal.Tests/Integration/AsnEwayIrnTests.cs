using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
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
/// R18 (2026-11-03, AC2) — the optional India e-way bill (number + date) and e-invoice IRN (number + date) references a
/// supplier can record on an ASN, through the REAL host.
///
/// <para>They behave exactly like the R11 shipment refs (invoice no. / bill of lading / packing list): optional at create,
/// numbers trimmed with blank stored as null, FULL-OVERWRITE on update (a caller that omits one clears it), and editable under
/// the same lifecycle gate (Draft / Rejected / Approved; locked from PendingApproval on). The numbers are capped at their column
/// widths (20 / 64) so an over-long value is a 400, not a SQL truncation 500. They are portal + packing-list data only and
/// never reach LN/IDM, so nothing here asserts on an outbound payload.</para>
///
/// <para>Every test seeds its own tagged supplier + PO, so no shared row is touched and the persistent test DB needs no clean-up.
/// Each value is read back BOTH through <c>GET /api/asns/{id}</c> and straight off the stored row, so neither a read-side echo
/// nor a write that never landed can pass.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnEwayIrnTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The column widths (asn.ewayBillNo varchar(20), asn.irnNo varchar(64)) the validators must enforce. Hard-coded on purpose:
    // the tests pin the contract, so a wrong cap in the validators fails here rather than being echoed back.
    private const int EwayBillNoWidth = 20;
    private const int IrnNoWidth = 64;

    private readonly IntegrationTestFixture _fx;
    public AsnEwayIrnTests(IntegrationTestFixture fx) => _fx = fx;

    /// <summary>The four refs as one comparable value (record equality). Each field differs between A and B, so a swapped assignment cannot hide.</summary>
    private sealed record Refs(string? EwayBillNo, DateOnly? EwayBillDate, string? IrnNo, DateOnly? IrnDate)
    {
        public static readonly Refs None = new(null, null, null, null);
        public static Refs Of(AsnDetailDto d) => new(d.EwayBillNo, d.EwayBillDate, d.IrnNo, d.IrnDate);
        public static Refs Of(Asn a) => new(a.EwayBillNo, a.EwayBillDate, a.IrnNo, a.IrnDate);
    }

    // Days above 12 on purpose: a month/day mix-up in date handling cannot parse to a valid-looking date and slip through.
    private static readonly Refs RefsA = new("271000123456", new DateOnly(2026, 11, 25), IrnOf("1a"), new DateOnly(2026, 11, 24));
    private static readonly Refs RefsB = new("271000654321", new DateOnly(2026, 12, 19), IrnOf("2b"), new DateOnly(2026, 12, 18));

    /// <summary>A realistic IRN: 64 hex characters (the prefix padded out).</summary>
    private static string IrnOf(string prefix) => prefix.PadRight(IrnNoWidth, 'f');

    /// <summary>A value of exactly <paramref name="length"/> characters ending in 'Z', so a silently truncated store fails the equality check.</summary>
    private static string OfLength(int length) => "Z".PadLeft(length, '0');

    // raw e-way no., expected e-way no., raw IRN, expected IRN — shared by the Create and Update normalisation tests.
    public static TheoryData<string?, string?, string?, string?> Normalisation => new()
    {
        { "  EWB-1  ", "EWB-1", "  IRN-1  ", "IRN-1" },   // surrounding whitespace is trimmed, per field
        { "EWB-1", "EWB-1", "   ", null },               // a whitespace-only number is stored as null ...
        { "   ", null, "IRN-1", "IRN-1" },               // ... independently of the other one
        { "", null, "", null },                          // an empty number too
    };

    // e-way no. to send, IRN to send, the field the 400 must name — each is exactly ONE character over its column width.
    public static TheoryData<string?, string?, string> OverLength => new()
    {
        { OfLength(EwayBillNoWidth + 1), null, "ewayBillNo" },
        { null, OfLength(IrnNoWidth + 1), "irnNo" },
    };

    // ═══════════════════════════════════ Create ═══════════════════════════════════

    [SkippableFact] // Happy path — all four supplied at create come back exactly, by API and on the row.
    public async Task Create_with_all_four_refs_returns_them_exactly_on_GET_and_stores_them()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();

        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));

        Refs.Of(created).Should().Be(RefsA, because: "the create response is built from the stored ASN");
        await AssertStoredAsync(supplier, created.Id, RefsA);
    }

    [SkippableFact] // Wire format — the two dates are plain calendar dates, and the four keys are camelCase.
    public async Task Get_serialises_the_two_dates_as_plain_yyyy_MM_dd_strings()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));

        var get = await supplier.GetAsync($"/api/asns/{created.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(get));
        using var doc = JsonDocument.Parse(await Body(get));
        var data = doc.RootElement.GetProperty("data");

        data.GetProperty("ewayBillNo").GetString().Should().Be(RefsA.EwayBillNo);
        data.GetProperty("ewayBillDate").GetString().Should().Be("2026-11-25", because: "a DateOnly crosses the wire as yyyy-MM-dd, no time or offset");
        data.GetProperty("irnNo").GetString().Should().Be(RefsA.IrnNo);
        data.GetProperty("irnDate").GetString().Should().Be("2026-11-24");
    }

    [SkippableFact] // Boundary of optionality — all four null: created OK, nulls returned (not "", not a min date).
    public async Task Create_without_the_refs_succeeds_and_all_four_read_back_null()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();

        var created = await CreateOkAsync(supplier, ProcureToPayFlow.SimpleAsn(setup));

        Refs.Of(created).Should().Be(Refs.None);
        await AssertStoredAsync(supplier, created.Id, Refs.None);
    }

    [SkippableTheory] // Normalised exactly like invoiceNo / billOfLading / packingList: trimmed, blank stored as null.
    [MemberData(nameof(Normalisation))]
    public async Task Create_trims_the_numbers_and_stores_a_blank_number_as_null(
        string? rawEway, string? expectedEway, string? rawIrn, string? expectedIrn)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();

        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA) with { EwayBillNo = rawEway, IrnNo = rawIrn });

        var expected = RefsA with { EwayBillNo = expectedEway, IrnNo = expectedIrn };
        Refs.Of(created).Should().Be(expected);
        await AssertStoredAsync(supplier, created.Id, expected);
    }

    [SkippableFact] // Boundary — exactly the column widths fit, and survive the varchar round trip whole.
    public async Task Create_accepts_numbers_of_exactly_20_and_64_characters_and_stores_them_whole()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var atTheCap = RefsA with { EwayBillNo = OfLength(EwayBillNoWidth), IrnNo = OfLength(IrnNoWidth) };

        var created = await CreateOkAsync(supplier, NewAsn(setup, atTheCap));

        Refs.Of(created).Should().Be(atTheCap);
        await AssertStoredAsync(supplier, created.Id, atTheCap);
    }

    [SkippableTheory] // Failure path — one character over the width is a 400 naming the field, not a SQL 500.
    [MemberData(nameof(OverLength))]
    public async Task Create_rejects_a_number_one_character_over_its_column_width_with_400_and_creates_nothing(
        string? eway, string? irn, string rejectedField)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();

        var resp = await supplier.PostAsJsonAsync("/api/asns", NewAsn(setup, RefsA) with { EwayBillNo = eway, IrnNo = irn });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        (await Body(resp)).Should().Contain(rejectedField);
        (await AsnCountForPoAsync(setup.PoId)).Should().Be(0, because: "a rejected request must persist nothing");
    }

    // ═══════════════════════════════════ Update — full overwrite ═══════════════════════════════════

    [SkippableFact] // Happy path — a PUT replaces all four with the values sent (each differs, so no field can stand in for another).
    public async Task Update_replaces_all_four_refs_with_the_values_sent()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));

        var updated = await UpdateOkAsync(supplier, created.Id, UpdateReq(setup, RefsB));

        Refs.Of(updated).Should().Be(RefsB);
        await AssertStoredAsync(supplier, created.Id, RefsB);
    }

    [SkippableFact] // Full-overwrite — the same rule as invoiceNo / billOfLading / packingList: null clears.
    public async Task Update_with_all_four_refs_null_clears_them()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));
        await AssertStoredAsync(supplier, created.Id, RefsA);   // precondition: they were really set

        var updated = await UpdateOkAsync(supplier, created.Id, UpdateReq(setup, Refs.None));

        Refs.Of(updated).Should().Be(Refs.None);
        await AssertStoredAsync(supplier, created.Id, Refs.None);
    }

    [SkippableTheory]
    [MemberData(nameof(Normalisation))]
    public async Task Update_trims_the_numbers_and_stores_a_blank_number_as_null(
        string? rawEway, string? expectedEway, string? rawIrn, string? expectedIrn)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));

        var updated = await UpdateOkAsync(supplier, created.Id, UpdateReq(setup, RefsB with { EwayBillNo = rawEway, IrnNo = rawIrn }));

        var expected = RefsB with { EwayBillNo = expectedEway, IrnNo = expectedIrn };
        Refs.Of(updated).Should().Be(expected);
        await AssertStoredAsync(supplier, created.Id, expected);
    }

    [SkippableFact] // Boundary — exactly the column widths are accepted on PUT too.
    public async Task Update_accepts_numbers_of_exactly_20_and_64_characters_and_stores_them_whole()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));
        var atTheCap = RefsB with { EwayBillNo = OfLength(EwayBillNoWidth), IrnNo = OfLength(IrnNoWidth) };

        var updated = await UpdateOkAsync(supplier, created.Id, UpdateReq(setup, atTheCap));

        Refs.Of(updated).Should().Be(atTheCap);
        await AssertStoredAsync(supplier, created.Id, atTheCap);
    }

    [SkippableTheory] // Failure path — validation runs before the handler, so a rejected PUT leaves the ASN as it was.
    [MemberData(nameof(OverLength))]
    public async Task Update_rejects_a_number_one_character_over_its_column_width_with_400_and_leaves_the_stored_refs_untouched(
        string? eway, string? irn, string rejectedField)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));

        var put = await supplier.PutAsJsonAsync($"/api/asns/{created.Id}", UpdateReq(setup, RefsB with { EwayBillNo = eway, IrnNo = irn }));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(put));
        (await Body(put)).Should().Contain(rejectedField);
        await AssertStoredAsync(supplier, created.Id, RefsA);
    }

    // ═══════════════════════════════════ Update — same edit gates as the other refs ═══════════════════════════════════

    [SkippableFact] // Approved stays editable (R14 D5): the supplier completes the shipment details after the buyer confirms.
    public async Task Update_edits_the_refs_of_an_Approved_asn_and_leaves_it_Approved()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));
        await SendForApprovalAsync(supplier, created.Id);
        await ApproveAsync(created.Id);

        var updated = await UpdateOkAsync(supplier, created.Id, UpdateReq(setup, RefsB));

        updated.AsnStatus.Should().Be(nameof(AsnStatus.Approved), because: "an edit must not drop a confirmed ASN back to Draft");
        Refs.Of(updated).Should().Be(RefsB);
        await AssertStoredAsync(supplier, created.Id, RefsB);
    }

    [SkippableFact] // Locked — nothing new loosens the gate: a PendingApproval ASN is read-only for these fields too.
    public async Task Update_of_a_pending_approval_asn_is_a_409_and_the_stored_refs_stay_put()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (setup, supplier) = await NewPoAsync();
        var created = await CreateOkAsync(supplier, NewAsn(setup, RefsA));
        await SendForApprovalAsync(supplier, created.Id);

        var put = await supplier.PutAsJsonAsync($"/api/asns/{created.Id}", UpdateReq(setup, RefsB));

        put.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await Body(put));
        (await Body(put)).Should().Contain(nameof(AsnStatus.PendingApproval));
        await AssertStoredAsync(supplier, created.Id, RefsA);
    }

    // ───────────────────────────────────────── helpers ─────────────────────────────────────────

    private async Task<(ProcureToPayFlow.Setup Setup, HttpClient Supplier)> NewPoAsync()
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        return (setup, supplier);
    }

    /// <summary>The standard single-line ASN carrying <paramref name="refs"/> (SimpleAsn itself sets none of the four).</summary>
    private static CreateAsnRequest NewAsn(ProcureToPayFlow.Setup setup, Refs refs)
        => ProcureToPayFlow.SimpleAsn(setup) with
        {
            EwayBillNo = refs.EwayBillNo, EwayBillDate = refs.EwayBillDate, IrnNo = refs.IrnNo, IrnDate = refs.IrnDate,
        };

    /// <summary>A same-lines/same-quantity update carrying <paramref name="refs"/>, so it is also legal on an Approved ASN (lines + quantities are frozen there).</summary>
    private static UpdateAsnRequest UpdateReq(ProcureToPayFlow.Setup setup, Refs refs)
        => new(
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
            TimeWindow: null, CarrierName: null, TrackingNumber: "TRK", VehicleNumber: null, DriverName: null,
            DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest> { new(setup.PoLineId, ShippedQty: setup.OrderQty, BatchNumber: null, ExpiryDate: null) },
            CarrierCode: ProcureToPayFlow.TestCarrierCode,
            EwayBillNo: refs.EwayBillNo, EwayBillDate: refs.EwayBillDate, IrnNo: refs.IrnNo, IrnDate: refs.IrnDate);

    private async Task<AsnDetailDto> CreateOkAsync(HttpClient supplier, CreateAsnRequest req)
    {
        var resp = await supplier.PostAsJsonAsync("/api/asns", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<AsnDetailDto>(resp)).Data!;
    }

    private async Task<AsnDetailDto> UpdateOkAsync(HttpClient supplier, Guid asnId, UpdateAsnRequest req)
    {
        var resp = await supplier.PutAsJsonAsync($"/api/asns/{asnId}", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<AsnDetailDto>(resp)).Data!;
    }

    private async Task SendForApprovalAsync(HttpClient supplier, Guid asnId)
    {
        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));
    }

    /// <summary>The mapped buyer confirms the PendingApproval ASN (the PO's buyer was assigned + mapped by the caller).</summary>
    private async Task ApproveAsync(Guid asnId)
    {
        var buyer = await SecurityTestHarness.ClientAsAsync(_fx, SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));
    }

    /// <summary>Asserts the four refs read back as <paramref name="expected"/> through GET /api/asns/id AND on the stored row.</summary>
    private async Task AssertStoredAsync(HttpClient supplier, Guid asnId, Refs expected)
    {
        var get = await supplier.GetAsync($"/api/asns/{asnId}");
        get.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(get));
        Refs.Of((await Read<AsnDetailDto>(get)).Data!).Should().Be(expected, because: "the detail endpoint reads back what was stored");
        Refs.Of(await LoadAsnAsync(asnId)).Should().Be(expected, because: "the row itself must hold the values, not just the read model");
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
