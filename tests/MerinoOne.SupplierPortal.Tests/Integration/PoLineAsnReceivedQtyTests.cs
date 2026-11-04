using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R18 (2026-11-03) — AC6: the PO detail's per-line "ASN received" figure (<c>PurchaseOrderLineDto.AsnReceivedQty</c>), on REAL SQL through
/// the real host. Receipts arrive the way production gets them — LN pushing <c>POST /api/integration/inbound/asn-update</c> — and the PO is
/// read back through <c>GET /api/purchase-orders/{id}</c> as the PO supplier's own (seccode-filtered) API user. Pinned: the figure is the SUM
/// of the received quantity over every ASN line of the PO line; each PO line carries only its own total; only reported figures are summed (a
/// line LN has reported nothing on is <c>null</c> — unknown — while a reported 0 is 0); every ASN counts whatever shipment status LN holds
/// it at EXCEPT a cancelled one, and a soft-deleted ASN line or ASN is left out too; and the read goes through the caller's row-level
/// rights rather than around them.
///
/// <para>The test DB is persistent: every test seeds its OWN uniquely tagged supplier / PO / ASNs directly through <see cref="AppDbContext"/>
/// (the PO and ASN numbers are globally unique) and never touches the fixture's shared rows, so re-runs and sibling tests are unaffected.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PoLineAsnReceivedQtyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string AsnUpdateUrl = "/api/integration/inbound/asn-update";

    // Every seeded PO carries these three lines; a test picks which ones its ASNs cover.
    private static readonly int[] Positions = { 10, 20, 30 };

    private readonly IntegrationTestFixture _fx;
    public PoLineAsnReceivedQtyTests(IntegrationTestFixture fx) => _fx = fx;

    // ── happy path: the sum over every ASN line of the PO line ──────────────────────────────────────────
    [SkippableFact]
    public async Task A_po_line_shows_the_sum_of_the_quantities_LN_received_across_all_its_asn_lines()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var first = await SeedAsnAsync(po, 10);
        var second = await SeedAsnAsync(po, 10);
        await ReportAsync(po.Supplier, (first, 10, 3m), (second, 10, 4m));

        (await ReadReceivedAsync(po))[10].Should().Be(7m, "3 received against the first ASN + 4 against the second");
    }

    // ── grouping: a PO line never picks up another PO line's quantities ─────────────────────────────────
    [SkippableFact]
    public async Task Each_po_line_shows_only_its_own_total_and_a_line_no_asn_covers_stays_null()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var tenAndTwenty = await SeedAsnAsync(po, 10, 20);
        var tenOnly = await SeedAsnAsync(po, 10);
        await ReportAsync(po.Supplier, (tenAndTwenty, 10, 3m), (tenAndTwenty, 20, 2m), (tenOnly, 10, 4m));

        var received = await ReadReceivedAsync(po);
        received[10].Should().Be(7m);
        received[20].Should().Be(2m, "line 20's total is its own; it does not absorb line 10's");
        received[30].Should().BeNull("no ASN covers line 30");
    }

    // ── boundary: only the reported figures are summed ──────────────────────────────────────────────────
    [SkippableFact]
    public async Task Only_the_reported_figures_are_summed_when_a_sibling_asn_line_has_no_receipt_yet()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var reported = await SeedAsnAsync(po, 10);
        await SeedAsnAsync(po, 10);     // covers the same PO line, but LN has reported nothing on it
        await ReportAsync(po.Supplier, (reported, 10, 5m));

        (await ReadReceivedAsync(po))[10].Should().Be(5m, "the sibling ASN line's missing figure adds nothing and does not blank the total");
    }

    // ── boundary: no figure is null, a reported 0 is 0 ──────────────────────────────────────────────────
    [SkippableFact]
    public async Task A_line_LN_has_reported_nothing_on_is_null_even_though_an_asn_covers_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var asn = await SeedAsnAsync(po, 10, 20);
        await ReportAsync(po.Supplier, (asn, 10, 6m));

        var received = await ReadReceivedAsync(po);
        received[10].Should().Be(6m, "the report landed, so the null below is not a failed arrangement");
        received[20].Should().BeNull("an ASN line exists on it but LN has reported no receipt — unknown, not zero");
        received[30].Should().BeNull("no ASN covers it at all");
    }

    [SkippableFact]
    public async Task A_receipt_LN_reports_as_zero_shows_zero_not_null()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var asn = await SeedAsnAsync(po, 10, 20);
        await ReportAsync(po.Supplier, (asn, 10, 0m));

        var received = await ReadReceivedAsync(po);
        received[10].Should().Be(0m, "LN reported 0 received: a known total, distinct from no report");
        received[20].Should().BeNull("LN reported nothing on this one");
    }

    // ── exclusions: only a cancelled ASN, and soft-deleted rows ─────────────────────────────────────────
    [SkippableFact]
    public async Task Every_asn_that_is_not_cancelled_counts_whatever_shipment_status_LN_reported()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var inTransit = await SeedAsnAsync(po, 10);
        var delivered = await SeedAsnAsync(po, 10);
        var received = await SeedAsnAsync(po, 10);
        var submitted = await SeedAsnAsync(po, 10);
        await ReportAsync(po.Supplier, "InTransit", (inTransit, 10, 3m));
        await ReportAsync(po.Supplier, "Delivered", (delivered, 10, 4m));
        await ReportAsync(po.Supplier, "Received", (received, 10, 5m));
        // A status the portal has no lifecycle state for: the endpoint still stores the receipt but leaves the ASN at Submitted.
        await ReportAsync(po.Supplier, "Partially Received", (submitted, 10, 2m));

        (await ReadReceivedAsync(po))[10].Should().Be(14m,
            "a receipt counts on an in-transit, delivered or received ASN, and equally on one still at Submitted because LN's status maps to no portal state");
    }

    [SkippableFact]
    public async Task A_cancelled_asns_received_qty_is_left_out()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var live = await SeedAsnAsync(po, 10, 20);
        var cancelled = await SeedAsnAsync(po, 10, 20);
        await ReportAsync(po.Supplier, (live, 10, 3m), (cancelled, 10, 4m), (cancelled, 20, 8m));
        await CancelAsnAsync(cancelled);

        var received = await ReadReceivedAsync(po);
        received[10].Should().Be(3m, "the cancelled ASN's 4 no longer counts");
        received[20].Should().BeNull("the only figure LN reported against line 20 sits on the cancelled ASN");
    }

    [SkippableFact]
    public async Task A_soft_deleted_asn_line_is_left_out()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var kept = await SeedAsnAsync(po, 10);
        var removed = await SeedAsnAsync(po, 10);
        await ReportAsync(po.Supplier, (kept, 10, 3m), (removed, 10, 4m));
        await SoftDeleteAsnLinesAsync(removed);

        (await ReadReceivedAsync(po))[10].Should().Be(3m, "the soft-deleted ASN line's 4 no longer counts");
    }

    [SkippableFact]
    public async Task The_lines_of_a_soft_deleted_asn_are_left_out()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var kept = await SeedAsnAsync(po, 10);
        var removed = await SeedAsnAsync(po, 10);
        await ReportAsync(po.Supplier, (kept, 10, 3m), (removed, 10, 4m));
        await SoftDeleteAsnAsync(removed);     // its lines stay live rows: only the ASN root's filter can hide them

        (await ReadReceivedAsync(po))[10].Should().Be(3m, "the soft-deleted ASN's 4 no longer counts");
    }

    // ── row-level security: the read goes through the caller's rights, not around them ──────────────────
    [SkippableFact]
    public async Task A_supplier_user_is_not_shown_received_qty_from_asn_lines_its_seccode_cannot_read()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var po = await SeedPoAsync();
        var own = await SeedAsnAsync(po, 10);

        // An ASN owned by ANOTHER supplier (its own seccode, on which the API user holds no right) whose line points at this PO line.
        // The portal cannot produce that — an ASN takes its seccode from the PO it covers — so it is arranged directly, to show the
        // figure honours the reader's row-level rights instead of bypassing them.
        var other = await SeedSupplierAsync(grantApiUser: false);
        var foreign = await SeedAsnAsync(other, po, 10);
        await ReportAsync(po.Supplier, (own, 10, 3m));
        await ReportAsync(other, (foreign, 10, 9m));

        (await ReadReceivedAsync(po))[10].Should().Be(3m, "the other supplier's ASN line is invisible to this supplier user");
        (await ReadReceivedAsync(po, SecurityTestHarness.Users.Admin))[10].Should().Be(12m,
            "a privileged reader sees every ASN line, so the foreign 9 is counted — the arrangement is real, not vacuous");
    }

    // ============================== arrange ==============================

    private sealed record SeededSupplier(string ErpCode, Guid SupplierId, Guid SeccodeId);
    private sealed record SeededPo(SeededSupplier Supplier, Guid PoId, IReadOnlyDictionary<int, Guid> LineIds);
    private sealed record SeededAsn(Guid Id, string Number, string Shipment);

    /// <summary>
    /// A tagged supplier with an ERP supplier code (the inbound endpoint resolves an ASN's supplier by it). <paramref name="grantApiUser"/>
    /// gives the Supplier-role API user read on the supplier's seccode — the PO's own supplier has it, any other does not.
    /// </summary>
    private async Task<SeededSupplier> SeedSupplierAsync(bool grantApiUser)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var seeded = await _fx.CreateSupplierAsync(tag, IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: grantApiUser ? SecurityTestHarness.Users.Supplier : null);
        var erpCode = $"BUS-{tag}";

        await WriteAsync(db => db.Suppliers.IgnoreQueryFilters().Where(s => s.Id == seeded.SupplierId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ErpCode, erpCode)));
        return new SeededSupplier(erpCode, seeded.SupplierId, seeded.SeccodeId);
    }

    /// <summary>A tagged supplier readable by the API user, with one PO of three lines (positions 10 / 20 / 30, no ASN on any yet).</summary>
    private async Task<SeededPo> SeedPoAsync()
    {
        var supplier = await SeedSupplierAsync(grantApiUser: true);
        var now = DateTime.UtcNow;

        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid(), PoNumber = $"PO-RQ-{Guid.NewGuid():N}", SupplierId = supplier.SupplierId, PoType = PoType.Material,
            PoDate = now.Date, PoStatus = PoStatus.Accepted, SeccodeId = supplier.SeccodeId,
            TenantId = IntegrationTestFixture.TenantId, TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed", CreatedOn = now,
        };
        var lines = Positions.Select(p => new PurchaseOrderLine
        {
            Id = Guid.NewGuid(), PurchaseOrderId = po.Id, PositionNo = p, SequenceNo = 1, ItemCode = $"ITEM-{p}",
            OrderUnit = "EA", OrderQty = 100, PriceUnit = 1, Price = 10, CreatedBy = "seed", CreatedOn = now,
        }).ToList();
        await WriteAsync(async db =>
        {
            db.PurchaseOrders.Add(po);
            db.PurchaseOrderLines.AddRange(lines);
            await db.SaveChangesAsync();
        });

        return new SeededPo(supplier, po.Id, lines.ToDictionary(l => l.PositionNo, l => l.Id));
    }

    /// <summary>
    /// A POSTED (Submitted) ASN of the PO's own supplier — the state ERP may report on — with one ASN line, and no receipt yet, on each of
    /// <paramref name="positions"/> of <paramref name="po"/>.
    /// </summary>
    private Task<SeededAsn> SeedAsnAsync(SeededPo po, params int[] positions) => SeedAsnAsync(po.Supplier, po, positions);

    /// <summary>The same, for an ASN owned by <paramref name="owner"/> — the PO's supplier, or another one to test visibility.</summary>
    private async Task<SeededAsn> SeedAsnAsync(SeededSupplier owner, SeededPo po, params int[] positions)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        var asn = new Asn
        {
            Id = Guid.NewGuid(), AsnNumber = $"ASN-RQ-{suffix}", PurchaseOrderId = po.PoId, SupplierId = owner.SupplierId,
            ExpectedDeliveryDate = now.Date.AddDays(1), AsnStatus = AsnStatus.Submitted, SeccodeId = owner.SeccodeId,
            TenantId = IntegrationTestFixture.TenantId, TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed", CreatedOn = now,
        };
        var lines = positions.Select(position => new AsnLine
        {
            Id = Guid.NewGuid(), AsnId = asn.Id, PurchaseOrderLineId = po.LineIds[position], ShippedQty = 5,
            PositionNo = position, SequenceNo = 1, CreatedBy = "seed", CreatedOn = now,
        }).ToList();
        await WriteAsync(async db =>
        {
            db.Asns.Add(asn);
            db.AsnLines.AddRange(lines);
            await db.SaveChangesAsync();
        });

        return new SeededAsn(asn.Id, asn.AsnNumber, $"INB-{suffix}");
    }

    /// <summary>LN reporting receipts against shipments it now holds as <c>Received</c>.</summary>
    private Task ReportAsync(SeededSupplier supplier, params (SeededAsn Asn, int PositionNo, decimal Qty)[] receipts)
        => ReportAsync(supplier, "Received", receipts);

    /// <summary>
    /// LN reporting receipts through the REAL inbound endpoint, with the shipment status it holds: one ASN record per ASN named, carrying
    /// the (position, qty) lines given for it. Asserts every record was applied, so a broken arrangement fails here rather than as a
    /// misleading read-side assertion.
    /// </summary>
    private async Task ReportAsync(SeededSupplier supplier, string status, params (SeededAsn Asn, int PositionNo, decimal Qty)[] receipts)
    {
        var records = receipts.GroupBy(r => r.Asn)
            .Select(g => new AsnUpdateRecord(supplier.ErpCode, g.Key.Number, g.Key.Shipment, status,
                g.Select(r => new AsnUpdateLineRecord(r.PositionNo, r.Qty, ActualDeliveryDate: null)).ToList()))
            .ToList();

        var resp = await _fx.CreateInboundClient().PostAsJsonAsync(AsnUpdateUrl,
            new PushAsnUpdatesRequest(IntegrationTestFixture.CompanyCode, records));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());

        var rows = (await Read<UpsertResultDto>(resp)).Data!.Rows;
        rows.Should().OnlyContain(r => r.Outcome == RowOutcome.Updated, because: string.Join("; ", rows.Select(r => $"{r.Code}={r.Outcome} {r.Error}")));
    }

    // Changes made AFTER LN has reported, written straight to the rows: the cancel endpoint would also reverse a shipped-qty consumption
    // these seeded ASNs never made, and no portal path soft-deletes an ASN or the lines of one LN has already reported on.

    private Task CancelAsnAsync(SeededAsn asn)
        => WriteAsync(db => db.Asns.IgnoreQueryFilters().Where(a => a.Id == asn.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AsnStatus, AsnStatus.Cancelled)));

    private Task SoftDeleteAsnLinesAsync(SeededAsn asn)
        => WriteAsync(db => db.AsnLines.IgnoreQueryFilters().Where(l => l.AsnId == asn.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsDeleted, true)));

    private Task SoftDeleteAsnAsync(SeededAsn asn)
        => WriteAsync(db => db.Asns.IgnoreQueryFilters().Where(a => a.Id == asn.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDeleted, true)));

    /// <summary>Runs <paramref name="write"/> against a fresh scoped <see cref="AppDbContext"/> of the real host.</summary>
    private async Task WriteAsync(Func<AppDbContext, Task> write)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        await write(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // ============================== read ==============================

    /// <summary>The PO's lines' <c>AsnReceivedQty</c> keyed by position, read through the real GET as <paramref name="userCode"/>.</summary>
    private async Task<IReadOnlyDictionary<int, decimal?>> ReadReceivedAsync(SeededPo po, string userCode = SecurityTestHarness.Users.Supplier)
    {
        var client = await _fx.ClientAsAsync(userCode, IntegrationTestFixture.CompanyId);
        var resp = await client.GetAsync($"/api/purchase-orders/{po.PoId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());

        var detail = (await Read<PurchaseOrderDetailDto>(resp)).Data!;
        return detail.Lines.ToDictionary(l => l.PositionNo, l => l.AsnReceivedQty);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
        => JsonSerializer.Deserialize<Result<T>>(await resp.Content.ReadAsStringAsync(), Json)!;
}
