using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Common;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Shipments.AsnListItemDto>;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R18 (2026-11-03, AC4) — ASN list search and the Expected-delivery day range (<c>GET /api/asns</c>), through the REAL host.
///
/// <para><c>search</c> also matches the number of any PO the ASN covers: the header PO, or a PO that only the multi-PO
/// junction carries. An ASN covering several matching POs is still ONE row, and <c>totalCount</c> counts it once.
/// <c>expectedFrom</c> / <c>expectedTo</c> are inclusive calendar days on the DISPLAY clock — the day the list's Expected
/// column shows (<c>AppTime.Date</c>). The date tests therefore put their ASNs on the first and last second of a local day:
/// on any zone with a UTC offset those instants straddle a UTC day edge, so a window cut at UTC midnight instead of
/// display-zone midnight fails them.</para>
///
/// <para>The test DB is persistent, so every arrangement here is a fresh tagged supplier, and each list call is scoped by it (or
/// by a search term unique to it); no seeded row, and no other suite's row, is read or mutated.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnListSearchTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Mid-June: no DST edge in any common zone, so Day-1 / Day / Day+1 are three plain 24-hour local days.
    private static readonly DateOnly Day = new(2027, 6, 15);

    // The read-only search tests share ONE arrangement: seeding three POs and three ASNs takes seconds on a busy DB and none of
    // those tests changes the data. (The test that edits an ASN builds its own.) Same pattern as ProcureToPayFlow's test carrier.
    private static readonly SemaphoreSlim SharedShapeGate = new(1, 1);
    private static Shape? _sharedShape;

    private readonly IntegrationTestFixture _fx;
    public AsnListSearchTests(IntegrationTestFixture fx) => _fx = fx;

    // ═══════════════════════════════════ expectedFrom / expectedTo — inclusive local days ═══════════════════════════════════

    [SkippableFact]
    public async Task Expected_to_includes_the_whole_to_day_and_excludes_the_next_day()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (po, client, asns) = await AsnsExpectedAtAsync(StoredAt(Day), StoredAt(Day, 23, 59, 59), StoredAt(Day.AddDays(1)));
        var (firstSecond, lastSecond, nextDay) = (asns[0], asns[1], asns[2]);
        var supplierId = po.Supplier.SupplierId;

        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedTo={Iso(Day)}"))
            .Should().BeEquivalentTo(new[] { firstSecond, lastSecond },
                because: "every instant of the 'to' day is in, and the first second of the next local day is out");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedTo={Iso(Day.AddDays(1))}"))
            .Should().BeEquivalentTo(new[] { firstSecond, lastSecond, nextDay },
                because: "moving 'to' on by a day brings that next day's row in");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedTo={Iso(Day.AddDays(-1))}"))
            .Should().BeEmpty(because: "the day before 'to' holds none of them — the first second of the 'to' day is not yesterday");
    }

    [SkippableFact]
    public async Task Expected_from_includes_the_whole_from_day_and_excludes_the_previous_day()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (po, client, asns) = await AsnsExpectedAtAsync(StoredAt(Day.AddDays(-1), 23, 59, 59), StoredAt(Day), StoredAt(Day, 23, 59, 59));
        var (previousDay, firstSecond, lastSecond) = (asns[0], asns[1], asns[2]);
        var supplierId = po.Supplier.SupplierId;

        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={Iso(Day)}"))
            .Should().BeEquivalentTo(new[] { firstSecond, lastSecond },
                because: "every instant of the 'from' day is in, and the last second of the previous local day is out");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={Iso(Day.AddDays(-1))}"))
            .Should().BeEquivalentTo(new[] { previousDay, firstSecond, lastSecond },
                because: "moving 'from' back by a day brings that previous day's row in");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={Iso(Day.AddDays(1))}"))
            .Should().BeEmpty(because: "the day after 'from' holds none of them — the last second of the 'from' day is not tomorrow");
    }

    [SkippableFact]
    public async Task Expected_from_and_to_on_the_same_day_select_exactly_that_local_day()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (po, client, asns) = await AsnsExpectedAtAsync(
            StoredAt(Day.AddDays(-1), 23, 59, 59), StoredAt(Day), StoredAt(Day, 23, 59, 59), StoredAt(Day.AddDays(1)));
        var supplierId = po.Supplier.SupplierId;

        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={Iso(Day)}&expectedTo={Iso(Day)}"))
            .Should().BeEquivalentTo(new[] { asns[1], asns[2] },
                because: "a one-day range is a valid range and holds both edges of that day, nothing from either neighbour");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={Iso(Day.AddDays(-1))}&expectedTo={Iso(Day.AddDays(1))}"))
            .Should().BeEquivalentTo(asns, because: "a three-day range holds all four");
    }

    [SkippableFact]
    public async Task Expected_bounds_on_the_first_and_last_day_of_the_calendar_are_open_ended_not_a_server_error()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (po, client, asns) = await AsnsExpectedAtAsync(StoredAt(Day, 12), StoredAt(Day.AddDays(1), 12));
        var supplierId = po.Supplier.SupplierId;
        var (firstDay, lastDay) = (Iso(DateOnly.MinValue), Iso(DateOnly.MaxValue));   // 0001-01-01 and 9999-12-31

        // 9999-12-31 is a valid 'to' (a common open-ended sentinel) yet has no next midnight to cut the window at — it must bound
        // nothing rather than throw. 0001-01-01 is its mirror at the other end.
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedTo={lastDay}"))
            .Should().BeEquivalentTo(asns, because: "the last day the calendar has is a valid 'to', and every date up to it is in");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={firstDay}"))
            .Should().BeEquivalentTo(asns, because: "the first day the calendar has is a valid 'from', and every date from it is in");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={firstDay}&expectedTo={lastDay}"))
            .Should().BeEquivalentTo(asns, because: "the widest range there is holds every row");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={Iso(Day.AddDays(1))}&expectedTo={lastDay}"))
            .Should().BeEquivalentTo(new[] { asns[1] }, because: "an open-ended 'to' still honours the 'from' bound");
        (await ListIdsAsync(client, $"supplierId={supplierId}&expectedFrom={lastDay}&expectedTo={lastDay}"))
            .Should().BeEmpty(because: "a one-day range on the last day is valid, and nothing is expected that far out");
    }

    [SkippableFact]
    public async Task Expected_from_later_than_expected_to_is_a_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (po, client, _) = await AsnsExpectedAtAsync(StoredAt(Day, 12));

        // One day inverted is the smallest bad range; the equal-days range above is the largest good one.
        var resp = await client.GetAsync(
            $"/api/asns?supplierId={po.Supplier.SupplierId}&expectedFrom={Iso(Day.AddDays(1))}&expectedTo={Iso(Day)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        (await Body(resp)).Should().Contain("Expected to must be on or after Expected from");
    }

    [SkippableTheory]
    [InlineData("expectedFrom", "2027-13-45")]
    [InlineData("expectedTo", "not-a-date")]
    public async Task A_date_that_is_not_a_calendar_day_is_a_400(string parameter, string value)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var resp = await client.GetAsync($"/api/asns?{parameter}={value}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: "an unparseable day is the caller's error, not an empty list");
        (await Body(resp)).Should().Contain(parameter, because: "the 400 names the parameter that could not be read");
    }

    [SkippableFact]
    public async Task A_slash_separated_expected_date_is_read_month_first_never_day_first()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();   // ASN-2 expects Day, 15 June

        // The query-string binder is culture-invariant, and the endpoint doc tells callers so: a slash-separated date is read month
        // first. 15 June is chosen because it can be read ONE way only — as MM/dd it is the day; as dd/MM there is no month 15.
        var monthFirst = Uri.EscapeDataString("06/15/2027");
        (await ListIdsAsync(s.Client, $"supplierId={s.SupplierId}&expectedFrom={monthFirst}&expectedTo={monthFirst}"))
            .Should().Equal(new[] { s.Asn2.Id }, because: "06/15/2027 is 15 June, the one day ASN-2 expects");

        var dayFirst = await s.Client.GetAsync(
            $"/api/asns?supplierId={s.SupplierId}&expectedTo={Uri.EscapeDataString("15/06/2027")}");
        dayFirst.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: "15/06/2027 has no month 15 — a day-first date is never guessed at");
    }

    // ═══════════════════════════════════ search — covered PO numbers ═══════════════════════════════════

    [SkippableFact]
    public async Task Search_by_the_number_of_a_po_an_asn_ships_finds_only_the_asns_covering_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();

        var byPo2 = await SearchAsync(s.Client, s.SupplierId, s.Po2.PoNumber);
        byPo2.Items.Should().ContainSingle().Which.Id.Should().Be(s.Asn2.Id,
            because: "only ASN-2 ships PO-2; ASN-1 and the multi-PO ASN do not");
        byPo2.Items.Single().PoNumber.Should().Be(s.Po2.PoNumber);

        (await SearchAsync(s.Client, s.SupplierId, s.Po1.PoNumber)).Items.Select(i => i.Id)
            .Should().BeEquivalentTo(new[] { s.Asn1.Id, s.Asn3.Id },
                because: "PO-1 is shipped by ASN-1 directly and by the multi-PO ASN through its junction");
    }

    [SkippableFact]
    public async Task Search_by_the_header_po_number_finds_an_asn_that_has_no_junction_rows()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // A legacy single-PO ASN carries only the header PurchaseOrderId — "empty junction for legacy ASNs" is part of the
        // model. Create always writes junction rows, so strip them to leave the header as the ONLY link to the PO.
        var (po, client, asns) = await AsnsExpectedAtAsync(StoredAt(Day, 12));
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.AsnPurchaseOrders.IgnoreQueryFilters().Where(j => j.AsnId == asns[0]).ExecuteDeleteAsync())
                .Should().Be(1, because: "the arrangement must leave the header PO as the ASN's only link");
        }

        var page = await SearchAsync(client, po.Supplier.SupplierId, po.PoNumber);

        page.Items.Should().ContainSingle().Which.Id.Should().Be(asns[0]);
        page.Items.Single().PoNumber.Should().Be(po.PoNumber, because: "the header PO is what the list row shows");
    }

    [SkippableFact]
    public async Task Search_by_a_po_carried_only_by_the_junction_of_a_multi_po_asn_finds_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();

        var page = await SearchAsync(s.Client, s.SupplierId, s.Po3.PoNumber);

        var row = page.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be(s.Asn3.Id);
        row.PoNumber.Should().BeNull(because: "a multi-PO ASN has no header PO — PO-3 is reachable ONLY through the junction");
        row.PoCount.Should().Be(2);
    }

    [SkippableFact]
    public async Task Search_matching_several_pos_of_one_asn_returns_the_asn_once()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();

        // "PO-P2P-" is in every PO number, so the multi-PO ASN matches through BOTH of its junction rows. A join-shaped
        // search would return it twice and report totalCount 4.
        var page = await SearchAsync(s.Client, s.SupplierId, "PO-P2P-");

        page.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { s.Asn1.Id, s.Asn2.Id, s.Asn3.Id });
        page.TotalCount.Should().Be(3, because: "each ASN counts once however many of its POs match");
    }

    [SkippableFact]
    public async Task Search_by_a_fragment_of_the_asn_number_finds_it_ignoring_surrounding_spaces()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();

        // An ASN number ends in its creation instant (yyyyMMddHHmmssfff). Five of the last seven characters — the minute's last
        // digit, the second, and the first two of the milliseconds — are a fragment from the MIDDLE of the number (neither
        // prefix nor suffix), and the sibling ASNs, each made by its own request tens of milliseconds later, cannot share it.
        var fragment = s.Asn1.AsnNumber[^6..^1];

        var page = await SearchAsync(s.Client, s.SupplierId, $"  {fragment}  ");

        page.Items.Should().ContainSingle().Which.Id.Should().Be(s.Asn1.Id);
    }

    [SkippableFact]
    public async Task Search_by_the_suppliers_legal_name_still_finds_its_asns()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();

        // No supplierId filter: the tagged legal name ("Test Supplier <tag>") is unique to this supplier, so the term alone
        // isolates its three ASNs — and it is in neither an ASN number nor a PO number, so only the name path can match.
        var resp = await s.Client.GetAsync($"/api/asns?pageSize=200&search={Uri.EscapeDataString($"Supplier {s.Po1.Tag}")}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var page = (await Read<ContractsPagedResult>(resp)).Data!;

        page.Items.Select(i => i.Id).Should().BeEquivalentTo(new[] { s.Asn1.Id, s.Asn2.Id, s.Asn3.Id });
    }

    [SkippableFact]
    public async Task Search_matching_nothing_returns_an_empty_page()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();

        var page = await SearchAsync(s.Client, s.SupplierId, $"NOPE-{Guid.NewGuid():N}");

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    [SkippableFact]
    public async Task A_po_removed_from_an_asn_by_an_edit_stops_matching_the_search()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await ArrangeShapeAsync();
        (await SearchAsync(s.Client, s.SupplierId, s.Po3.PoNumber)).Items.Should().ContainSingle()
            .Which.Id.Should().Be(s.Asn3.Id, because: "before the edit the multi-PO ASN covers PO-3");

        // Drop PO-3's line: the edit rebuilds the junction, soft-deleting the PO-3 row, and the ASN becomes a plain PO-1 ASN.
        var edit = new UpdateAsnRequest(
            ExpectedDeliveryDate: StoredAt(Day.AddDays(1), 12),
            TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest> { new(s.Po1.PoLineId, ShippedQty: 1, BatchNumber: null, ExpiryDate: null) });
        var put = await s.Client.PutAsJsonAsync($"/api/asns/{s.Asn3.Id}", edit);
        put.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(put));

        (await SearchAsync(s.Client, s.SupplierId, s.Po3.PoNumber)).Items
            .Should().BeEmpty(because: "a PO the ASN no longer covers must not keep matching through its soft-deleted junction row");
        (await SearchAsync(s.Client, s.SupplierId, s.Po1.PoNumber)).Items.Select(i => i.Id)
            .Should().BeEquivalentTo(new[] { s.Asn1.Id, s.Asn3.Id }, because: "PO-1 is still covered by both");
    }

    [SkippableFact]
    public async Task Search_and_expected_range_narrow_the_list_together()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();

        // PO-1's search alone is {ASN-1 (Day+2), ASN-3 (Day+1)}; the range up to Day+1 alone is {ASN-2 (Day), ASN-3 (Day+1)}.
        var resp = await s.Client.GetAsync(
            $"/api/asns?supplierId={s.SupplierId}&search={Uri.EscapeDataString(s.Po1.PoNumber)}&expectedTo={Iso(Day.AddDays(1))}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var page = (await Read<ContractsPagedResult>(resp)).Data!;

        page.Items.Should().ContainSingle().Which.Id.Should().Be(s.Asn3.Id, because: "the two filters AND together");
        page.TotalCount.Should().Be(1);
    }

    // ═══════════════════════════════════ no new parameters — behaviour unchanged ═══════════════════════════════════

    [SkippableFact]
    public async Task Without_the_new_parameters_the_list_keeps_every_asn_newest_expected_date_first()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();   // ASN-1 expects Day+2, ASN-3 Day+1, ASN-2 Day

        var all = await ListAsync(s.Client, $"supplierId={s.SupplierId}");
        all.Items.Select(i => i.Id).Should().Equal(new[] { s.Asn1.Id, s.Asn3.Id, s.Asn2.Id },
            because: "the list is still ordered by Expected delivery date, newest first");
        all.TotalCount.Should().Be(3);

        (await ListAsync(s.Client, $"supplierId={s.SupplierId}&status=Draft")).TotalCount
            .Should().Be(3, because: "the status filter is untouched — all three are Drafts");
        (await ListAsync(s.Client, $"supplierId={s.SupplierId}&status=Cancelled")).Items
            .Should().BeEmpty(because: "and a status none of them has still matches nothing");
        (await ListAsync(s.Client, $"supplierId={s.SupplierId}&search=%20%20")).TotalCount
            .Should().Be(3, because: "a blank search is still no search");
    }

    [SkippableFact]
    public async Task The_purchase_order_filter_still_matches_header_and_junction_pos()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SharedShapeAsync();

        (await ListIdsAsync(s.Client, $"supplierId={s.SupplierId}&purchaseOrderId={s.Po1.PoId}"))
            .Should().BeEquivalentTo(new[] { s.Asn1.Id, s.Asn3.Id }, because: "PO-1 is ASN-1's header PO and one of the multi-PO ASN's junction POs");
        (await ListIdsAsync(s.Client, $"supplierId={s.SupplierId}&purchaseOrderId={s.Po3.PoId}"))
            .Should().BeEquivalentTo(new[] { s.Asn3.Id }, because: "PO-3 is reachable only through the junction");
    }

    // ───────────────────────────────────────── helpers ─────────────────────────────────────────

    /// <summary>
    /// The stored (UTC) instant of a wall-clock time on the DISPLAY clock — what <c>AppTime.Date</c> renders back as that local
    /// day, so what the list's Expected column shows. Midnight and 23:59:59 of one local day fall on opposite sides of a UTC
    /// day edge in any zone that has an offset.
    /// </summary>
    private static DateTime StoredAt(DateOnly day, int hour = 0, int minute = 0, int second = 0)
        => AppTime.ToUtc(day.ToDateTime(new TimeOnly(hour, minute, second)));

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>One fresh supplier with one shippable PO, and a Draft ASN over it for each stored instant given, in order.</summary>
    private async Task<(ProcureToPayFlow.Setup Po, HttpClient Client, List<Guid> AsnIds)> AsnsExpectedAtAsync(params DateTime[] expectedUtc)
    {
        var po = await ProcureToPayFlow.SeedPoAsync(_fx);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var ids = new List<Guid>();
        foreach (var expected in expectedUtc)
            ids.Add((await CreateAsnAsync(client, expected, po)).Id);
        return (po, client, ids);
    }

    /// <summary>
    /// The search arrangement, all under one fresh supplier: ASN-1 ships PO-1, ASN-2 ships PO-2, and ASN-3 is a multi-PO ASN over
    /// PO-1 + PO-3 (no header PO — PO-3 is reachable only through the junction). Expected dates are distinct (ASN-1 Day+2,
    /// ASN-3 Day+1, ASN-2 Day), so newest-first is ASN-1, ASN-3, ASN-2.
    /// </summary>
    private async Task<Shape> ArrangeShapeAsync()
    {
        var po1 = await ProcureToPayFlow.SeedPoAsync(_fx);
        var po2 = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, po1.Supplier, confirm: true);
        var po3 = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, po1.Supplier, confirm: true);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var asn1 = await CreateAsnAsync(client, StoredAt(Day.AddDays(2), 12), po1);
        var asn2 = await CreateAsnAsync(client, StoredAt(Day, 12), po2);
        var asn3 = await CreateAsnAsync(client, StoredAt(Day.AddDays(1), 12), po1, po3);
        return new Shape(client, po1, po2, po3, asn1, asn2, asn3);
    }

    private async Task<Shape> SharedShapeAsync()
    {
        await SharedShapeGate.WaitAsync();
        try { return _sharedShape ??= await ArrangeShapeAsync(); }
        finally { SharedShapeGate.Release(); }
    }

    private sealed record Shape(
        HttpClient Client,
        ProcureToPayFlow.Setup Po1, ProcureToPayFlow.Setup Po2, ProcureToPayFlow.Setup Po3,
        AsnDetailDto Asn1, AsnDetailDto Asn2, AsnDetailDto Asn3)
    {
        public Guid SupplierId => Po1.Supplier.SupplierId;
    }

    /// <summary>Creates a Draft ASN shipping one unit of each given PO's line — a single-PO ASN, or a multi-PO one for several.</summary>
    private async Task<AsnDetailDto> CreateAsnAsync(HttpClient client, DateTime expectedUtc, params ProcureToPayFlow.Setup[] pos)
    {
        var request = ProcureToPayFlow.SimpleAsn(pos[0], shippedQty: 1) with { ExpectedDeliveryDate = expectedUtc };
        if (pos.Length > 1)
            request = request with
            {
                PurchaseOrderId = null,
                PurchaseOrderIds = pos.Select(p => p.PoId).ToList(),
                Lines = pos.Select(p => new CreateAsnLineRequest(p.PoLineId, ShippedQty: 1, BatchNumber: null, ExpiryDate: null)).ToList(),
            };

        var resp = await client.PostAsJsonAsync("/api/asns", request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<AsnDetailDto>(resp)).Data!;
    }

    private async Task<ContractsPagedResult> ListAsync(HttpClient client, string query)
    {
        var resp = await client.GetAsync($"/api/asns?{query}&pageSize=200");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<ContractsPagedResult>(resp)).Data!;
    }

    private async Task<List<Guid>> ListIdsAsync(HttpClient client, string query)
        => (await ListAsync(client, query)).Items.Select(i => i.Id).ToList();

    private Task<ContractsPagedResult> SearchAsync(HttpClient client, Guid supplierId, string term)
        => ListAsync(client, $"supplierId={supplierId}&search={Uri.EscapeDataString(term)}");

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
