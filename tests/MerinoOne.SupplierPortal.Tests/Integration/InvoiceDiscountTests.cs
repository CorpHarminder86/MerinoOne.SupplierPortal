using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R19 (AC3) — the PO line discount is applied to the STORED invoice amounts. The PO line's <c>DiscountAmount</c> is
/// PER UNIT and <c>PriceUnit</c> is the GROSS unit price, so an invoice line stores the TOTAL discount
/// (perUnit x billedQty) and a <c>LineAmount</c> that is the TAXABLE value (gross - discount); tax is levied on it and
/// the header is the sum of the lines. Covers the ASN-generated draft, the Draft line edit, the manual create, and the
/// boundary that a Submitted invoice is never recomputed.
///
/// <para>Money path: fresh tagged supplier/PO/ASN per test (the test DB persists between runs).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InvoiceDiscountTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public InvoiceDiscountTests(IntegrationTestFixture fx) => _fx = fx;

    // ── 1. discounted PO line: gross 800 - discount 80 = taxable 720; 18% tax = 129.60; net 849.60 ─────
    [SkippableFact]
    public async Task Asn_generated_draft_applies_the_per_unit_po_discount_to_the_stored_amounts()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, invoiceId, _) = await CreateDraftAsync(discountAmount: 10m, discountPct: 10m, shippedQty: 8m);

        var detail = await GetInvoiceAsync(client, invoiceId);
        var line = detail.Lines.Single();
        line.BilledQty.Should().Be(8m);
        line.UnitPrice.Should().Be(100m, because: "UnitPrice stays the GROSS PO price-unit");
        line.DiscountPct.Should().Be(10m, because: "the PO line's discount percentage is snapshotted");
        line.DiscountAmount.Should().Be(80m, because: "per-unit discount 10 x billed qty 8");
        line.LineAmount.Should().Be(720m, because: "taxable = 8 x 100 - 80");
        line.TaxRatePct.Should().Be(18m);
        line.TaxAmount.Should().Be(129.60m, because: "tax is levied on the taxable amount (720 x 18%)");

        detail.InvoiceAmount.Should().Be(720m);
        detail.TaxAmount.Should().Be(129.60m);
        detail.NetAmount.Should().Be(849.60m);
    }

    // ── 2. no discount ⇒ behaviour unchanged ────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Asn_generated_draft_without_a_po_discount_is_unchanged()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, invoiceId, _) = await CreateDraftAsync(discountAmount: 0m, discountPct: 0m, shippedQty: 8m);

        var detail = await GetInvoiceAsync(client, invoiceId);
        var line = detail.Lines.Single();
        line.DiscountAmount.Should().Be(0m);
        line.LineAmount.Should().Be(800m, because: "no discount ⇒ qty x price");
        line.TaxAmount.Should().Be(144m);
        detail.InvoiceAmount.Should().Be(800m);
        detail.TaxAmount.Should().Be(144m);
        detail.NetAmount.Should().Be(944m);
    }

    // ── 3. Draft edit with a lower billed qty re-derives discount / taxable / tax / header ──────────────
    [SkippableFact]
    public async Task Draft_update_with_a_lower_billed_qty_recomputes_discount_amount_tax_and_header()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, invoiceId, _) = await CreateDraftAsync(discountAmount: 10m, discountPct: 10m, shippedQty: 8m);
        var before = await GetInvoiceAsync(client, invoiceId);
        var line = before.Lines.Single();

        var put = new UpdateInvoiceRequest(before.InvoiceNumber, before.InvoiceDate, null, null, null, null,
            new List<UpdateInvoiceLineRequest> { new(line.Id, 5m, null) });
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{invoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));
        var after = (await Read<InvoiceDetailDto>(putResp)).Data!;

        var updated = after.Lines.Single();
        updated.BilledQty.Should().Be(5m);
        updated.DiscountPct.Should().Be(10m);
        updated.DiscountAmount.Should().Be(50m, because: "per-unit discount 10 x the NEW billed qty 5");
        updated.LineAmount.Should().Be(450m, because: "taxable = 5 x 100 - 50");
        updated.TaxAmount.Should().Be(81m, because: "450 x 18%");
        after.InvoiceAmount.Should().Be(450m);
        after.TaxAmount.Should().Be(81m);
        after.NetAmount.Should().Be(531m);
    }

    // ── 4. boundary: a Submitted invoice is never recomputed ────────────────────────────────────────────
    [SkippableFact]
    public async Task Submitted_invoice_is_not_recomputed_by_a_later_po_discount_change_or_a_retry()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, invoiceId, setup) = await CreateDraftAsync(discountAmount: 10m, discountPct: 10m, shippedQty: 8m);

        var draft = await GetInvoiceAsync(client, invoiceId);
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{invoiceId}",
            new UpdateInvoiceRequest($"INV-DISC-{setup.Tag}", DateTime.UtcNow.Date, null, null, null, null));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));
        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{invoiceId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var frozen = (await Read<InvoiceDetailDto>(submitResp)).Data!;
        frozen.IsLocked.Should().BeTrue();
        frozen.InvoiceAmount.Should().Be(draft.InvoiceAmount, because: "submit re-freezes the SAME discounted amounts");
        frozen.NetAmount.Should().Be(849.60m);

        // The PO line's discount changes AFTER submit — the stored invoice must not move.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pol = await db.PurchaseOrderLines.IgnoreQueryFilters().FirstAsync(l => l.Id == setup.PoLineId);
            pol.DiscountAmount = 50m;
            await db.SaveChangesAsync();
        }

        // A Submitted invoice cannot be edited (409) …
        var lineId = frozen.Lines.Single().Id;
        var editResp = await client.PutAsJsonAsync($"/api/invoices/{invoiceId}",
            new UpdateInvoiceRequest(frozen.InvoiceNumber, frozen.InvoiceDate, null, null, null, null,
                new List<UpdateInvoiceLineRequest> { new(lineId, 2m, null) }));
        editResp.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await Body(editResp));

        // … and the ASN-generation retry is idempotent (returns the existing invoice, creates / recomputes nothing).
        var asnId = frozen.AsnId!.Value;
        var retryResp = await client.PostAsJsonAsync("/api/invoices/from-asn", new CreateInvoiceFromAsnRequest(asnId));
        retryResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(retryResp));
        (await Read<InvoiceDetailDto>(retryResp)).Data!.Id.Should().Be(invoiceId);

        var reread = await GetInvoiceAsync(client, invoiceId);
        var line = reread.Lines.Single();
        line.DiscountAmount.Should().Be(80m);
        line.LineAmount.Should().Be(720m);
        line.TaxAmount.Should().Be(129.60m);
        reread.InvoiceAmount.Should().Be(720m);
        reread.TaxAmount.Should().Be(129.60m);
        reread.NetAmount.Should().Be(849.60m);
    }

    // ── 5. manual create: the server snapshots the discount and ignores the client's LineAmount ─────────
    [SkippableFact]
    public async Task Manual_create_snapshots_the_po_discount_and_recomputes_line_amount_server_side()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, discountAmount: 10m, discountPct: 10m);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // The client keys the GROSS amount (10 x 100 = 1000) and tax on gross (18% = 180) — the server must not trust either.
        var gross = decimal.Round(setup.OrderQty * setup.PriceUnit, 2);
        var body = new CreateInvoiceRequest(
            setup.PoId, null, $"INV-DISCMAN-{setup.Tag}", DateTime.UtcNow.Date,
            InvoiceAmount: gross, TaxAmount: 180m, NetAmount: gross,
            CurrencyCode: "INR", MatchingType: nameof(MatchingType.TwoWay),
            EInvoiceIrn: null, EInvoiceAckNo: null, EWayBillNumber: null, Notes: null,
            Lines: new List<CreateInvoiceLineRequest>
            {
                new(setup.PoLineId, setup.ItemCode, null, setup.OrderQty, setup.PriceUnit, gross, null, 180m),
            });
        var resp = await client.PostAsJsonAsync("/api/invoices", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var detail = (await Read<InvoiceDetailDto>(resp)).Data!;

        var line = detail.Lines.Single();
        line.DiscountPct.Should().Be(10m);
        line.DiscountAmount.Should().Be(100m, because: "per-unit discount 10 x billed qty 10");
        line.LineAmount.Should().Be(900m, because: "server-recomputed taxable = 1000 - 100, not the client's 1000");
        line.TaxRatePct.Should().Be(18m, because: "the governed rate is resolved server-side from the PO line's tax");
        line.TaxId.Should().Be(setup.TaxId);
        line.TaxAmount.Should().Be(162m, because: "tax = 18% of the NET 900, not the client's 180 on gross");
        detail.InvoiceAmount.Should().Be(900m);
        detail.TaxAmount.Should().Be(162m);
        detail.NetAmount.Should().Be(1062m);
    }

    // ── 6. a pre-R19 Draft (gross LineAmount, DiscountAmount 0) is stored net-of-discount once submitted ──
    [SkippableFact]
    public async Task Submit_rederives_a_pre_r19_draft_to_net_of_discount_amounts()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, invoiceId, setup) = await CreateDraftAsync(discountAmount: 10m, discountPct: 10m, shippedQty: 8m);

        // Simulate a Draft generated BEFORE R19: gross line amount, no discount, header from the gross figures.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var inv = await db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == invoiceId);
            var l = await db.InvoiceLines.IgnoreQueryFilters().SingleAsync(x => x.InvoiceId == invoiceId);
            l.DiscountPct = null;
            l.DiscountAmount = 0m;
            l.LineAmount = 800m;
            l.TaxAmount = 144m;
            inv.InvoiceAmount = 800m;
            inv.TaxAmount = 144m;
            inv.NetAmount = 944m;
            await db.SaveChangesAsync();
        }

        var putResp = await client.PutAsJsonAsync($"/api/invoices/{invoiceId}",
            new UpdateInvoiceRequest($"INV-PRE19-{setup.Tag}", DateTime.UtcNow.Date, null, null, null, null));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));
        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{invoiceId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = (await Read<InvoiceDetailDto>(submitResp)).Data!;

        var line = submitted.Lines.Single();
        line.DiscountPct.Should().Be(10m);
        line.DiscountAmount.Should().Be(80m);
        line.LineAmount.Should().Be(720m);
        line.TaxAmount.Should().Be(129.60m);
        submitted.InvoiceAmount.Should().Be(720m);
        submitted.TaxAmount.Should().Be(129.60m);
        submitted.NetAmount.Should().Be(849.60m);
    }

    // ── 7. pct-only PO line (DiscountAmount 0, DiscountPct 10, PriceUnit 70) ⇒ discount derived from the pct ──
    [SkippableFact]
    public async Task Asn_generated_draft_derives_the_discount_from_a_pct_only_po_line()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, priceUnit: 70m, discountAmount: 0m, discountPct: 10m);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, 8m));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;
        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, client, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var invoiceId = (await Read<AsnDetailDto>(submitResp)).Data!.DraftInvoiceId!.Value;

        var line = (await GetInvoiceAsync(client, invoiceId)).Lines.Single();
        line.BilledQty.Should().Be(8m);
        line.UnitPrice.Should().Be(70m);
        line.DiscountPct.Should().Be(10m);
        line.DiscountAmount.Should().Be(56m, because: "pct-only: per-unit 70 x 10% = 7, x billed qty 8");
        line.LineAmount.Should().Be(504m, because: "taxable = 8 x 70 - 56 (net extended)");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Seeds a discounted PO (qty 10 @ 100, 18% tax), ships <paramref name="shippedQty"/> and returns the ASN-generated draft.</summary>
    private async Task<(HttpClient Client, Guid InvoiceId, ProcureToPayFlow.Setup Setup)> CreateDraftAsync(
        decimal discountAmount, decimal discountPct, decimal shippedQty)
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, discountAmount: discountAmount, discountPct: discountPct);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, client, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var invoiceId = (await Read<AsnDetailDto>(submitResp)).Data!.DraftInvoiceId!.Value;
        return (client, invoiceId, setup);
    }

    private static async Task<InvoiceDetailDto> GetInvoiceAsync(HttpClient client, Guid invoiceId)
    {
        var resp = await client.GetAsync($"/api/invoices/{invoiceId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<InvoiceDetailDto>(resp)).Data!;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
