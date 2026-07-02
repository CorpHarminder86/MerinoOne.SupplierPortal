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
/// R6 — Draft-invoice edit + submit lifecycle through the REAL host. The draft is the one auto-generated at ASN
/// approve (placeholder number <c>DRAFT-{asnNumber}-1</c>, provisional tax snapshot). Editing it (PUT — header +
/// the new per-line billedQty/tax reselect) then submitting (/submit) re-resolves + FREEZES the tax rate, takes
/// the per-PO-line over-invoice reservation and runs local matching (TwoWay ⇒ <b>Matched</b>). Submitting with the
/// placeholder number is a 400; a rate drift between draft and submit rides the response <c>notices</c>; the
/// frozen snapshot never changes after submit.
///
/// <para>Money path: scope gate OFF; fresh tagged supplier/PO/ASN per test.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InvoiceLifecycleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public InvoiceLifecycleTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Edit_draft_invoice_then_submit_lands_matched_and_reserves_billed_qty()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, setup) = await CreateDraftInvoiceAsync();

        // PUT a real invoice number + date.
        var realNumber = $"INV-REAL-{setup.Tag}";
        var put = new UpdateInvoiceRequest(realNumber, DateTime.UtcNow.Date, null, null, null, "edited");
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{draftInvoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));
        (await Read<InvoiceDetailDto>(putResp)).Data!.InvoiceNumber.Should().Be(realNumber);

        // Submit → local matching (TwoWay, reservation passes) ⇒ Matched; submittedAt stamped.
        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{draftInvoiceId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = await Read<InvoiceDetailDto>(submitResp);
        submitted.Data!.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Matched),
            because: "R6 local matching advances a passing TwoWay submit straight to Matched");
        submitted.Data!.InvoiceNumber.Should().Be(realNumber);
        submitted.Data!.SubmittedAt.Should().NotBeNull();
        submitted.Notices.Should().BeEmpty(because: "no rate drift happened between draft and submit");

        // The atomic reservation consumed the PO line's invoiceable balance.
        (await InvoicedToDate(setup.PoLineId)).Should().Be(setup.OrderQty,
            because: "submit reserves billedQty onto PurchaseOrderLine.invoicedQtyToDate");
        // RemainingQty reads 0 both because the balance is consumed and because the invoice is locked.
        var detail = await Read<InvoiceDetailDto>(await client.GetAsync($"/api/invoices/{draftInvoiceId}"));
        detail.Data!.Lines.Should().OnlyContain(l => l.RemainingQty == 0m);
        detail.Data!.IsLocked.Should().BeTrue();
    }

    [SkippableFact]
    public async Task Submitting_draft_invoice_with_placeholder_number_is_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, _) = await CreateDraftInvoiceAsync();

        // No PUT — the invoice still has the "DRAFT-…" placeholder, so submit must be rejected.
        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{draftInvoiceId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the draft placeholder number must be replaced with a real number before submit");
        (await Read<InvoiceDetailDto>(submitResp)).Errors.Should().Contain(e => e.Contains("invoice number", StringComparison.OrdinalIgnoreCase));
    }

    // ── (f) rate drift between draft and submit ⇒ line re-frozen at the NEW rate + advisory notice ─────
    [SkippableFact]
    public async Task Rate_drift_between_draft_and_submit_freezes_new_rate_and_returns_notice()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, setup) = await CreateDraftInvoiceAsync();

        // The governed rate changes AFTER the draft was generated at 18%.
        await SetTaxRateAsync(setup.TaxId!.Value, 12m);

        var realNumber = $"INV-DRIFT-{setup.Tag}";
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{draftInvoiceId}",
            new UpdateInvoiceRequest(realNumber, DateTime.UtcNow.Date, null, null, null, null));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));

        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{draftInvoiceId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = await Read<InvoiceDetailDto>(submitResp);

        submitted.Notices.Should().ContainSingle(n => n.Contains(setup.TaxCode!) && n.Contains("rate changed"),
            because: "the drift is applied (freeze = last write) and surfaced as an advisory");
        var line = submitted.Data!.Lines.Single();
        line.TaxRatePct.Should().Be(12m, because: "submit re-resolves and freezes the CURRENT rate");
        var expectedTax = decimal.Round(line.LineAmount * 12m / 100m, 2);
        line.TaxAmount.Should().Be(expectedTax);
        submitted.Data!.TaxAmount.Should().Be(expectedTax, because: "header totals are recomputed with the frozen rate");
        submitted.Data!.NetAmount.Should().Be(submitted.Data!.InvoiceAmount + expectedTax);
    }

    // ── (e) snapshot immutability: a rate edit AFTER submit never changes the frozen invoice ───────────
    [SkippableFact]
    public async Task Tax_rate_edit_after_submit_does_not_change_the_frozen_snapshot()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, setup) = await CreateDraftInvoiceAsync();
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{draftInvoiceId}",
            new UpdateInvoiceRequest($"INV-FRZ-{setup.Tag}", DateTime.UtcNow.Date, null, null, null, null));
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));
        var submitResp = await client.PostAsJsonAsync($"/api/invoices/{draftInvoiceId}/submit", new SubmitInvoiceRequest());
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var frozen = (await Read<InvoiceDetailDto>(submitResp)).Data!;

        // The governed rate changes AFTER submit — the frozen snapshot must not move.
        await SetTaxRateAsync(setup.TaxId!.Value, 28m);

        var reread = (await Read<InvoiceDetailDto>(await client.GetAsync($"/api/invoices/{draftInvoiceId}"))).Data!;
        reread.Lines.Single().TaxRatePct.Should().Be(frozen.Lines.Single().TaxRatePct,
            because: "the line snapshot is frozen at submit — reads never re-join the tax master");
        reread.TaxAmount.Should().Be(frozen.TaxAmount);
        reread.NetAmount.Should().Be(frozen.NetAmount);
    }

    // ── (g) Draft line tax reselect ⇒ code/desc/rate re-resolved server-side + amounts recomputed ──────
    [SkippableFact]
    public async Task Reselecting_a_tax_on_a_draft_line_reresolves_and_recomputes()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, setup) = await CreateDraftInvoiceAsync();
        var otherTaxCode = $"GST5-{setup.Tag}";
        var otherTaxId = await _fx.CreateTaxAsync(otherTaxCode, 5m);

        var detail = (await Read<InvoiceDetailDto>(await client.GetAsync($"/api/invoices/{draftInvoiceId}"))).Data!;
        var line = detail.Lines.Single();

        var put = new UpdateInvoiceRequest(detail.InvoiceNumber, detail.InvoiceDate, null, null, null, null,
            new List<UpdateInvoiceLineRequest> { new(line.Id, line.BilledQty, otherTaxId) });
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{draftInvoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));
        var updated = (await Read<InvoiceDetailDto>(putResp)).Data!;

        var updatedLine = updated.Lines.Single();
        updatedLine.TaxId.Should().Be(otherTaxId);
        updatedLine.TaxCode.Should().Be(otherTaxCode, because: "code/description/rate come from the master, never the client");
        updatedLine.TaxRatePct.Should().Be(5m);
        updatedLine.TaxAmount.Should().Be(decimal.Round(updatedLine.LineAmount * 5m / 100m, 2));
        updated.TaxAmount.Should().Be(updatedLine.TaxAmount);
        updated.NetAmount.Should().Be(updated.InvoiceAmount + updated.TaxAmount);
    }

    // ── (n) billedQty over the live remaining balance ⇒ 400 naming the line ─────────────────────────────
    [SkippableFact]
    public async Task Editing_billed_qty_over_the_remaining_balance_is_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, setup) = await CreateDraftInvoiceAsync();
        var detail = (await Read<InvoiceDetailDto>(await client.GetAsync($"/api/invoices/{draftInvoiceId}"))).Data!;
        var line = detail.Lines.Single();
        line.RemainingQty.Should().Be(setup.OrderQty, because: "nothing is invoiced yet — remaining = shipped");

        var put = new UpdateInvoiceRequest(detail.InvoiceNumber, detail.InvoiceDate, null, null, null, null,
            new List<UpdateInvoiceLineRequest> { new(line.Id, line.RemainingQty + 1, line.TaxId) });
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{draftInvoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "billedQty is server-capped at shippedQtyToDate − invoicedQtyToDate");
        (await Read<InvoiceDetailDto>(putResp)).Errors.Should().Contain(e => e.Contains(setup.ItemCode),
            because: "the 400 names the offending line");
    }

    // ── (fix 6) unchanged TaxId must NEVER hit the resolver — a rate-cleared master can't block edits ──
    [SkippableFact]
    public async Task Put_with_unchanged_tax_id_succeeds_and_preserves_snapshot_when_master_rate_was_cleared()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, draftInvoiceId, setup) = await CreateDraftInvoiceAsync();

        // The governed master loses its rate AFTER the draft froze 18% (e.g. an admin/LN wipe).
        await SetTaxRateAsync(setup.TaxId!.Value, null);

        var detail = (await Read<InvoiceDetailDto>(await client.GetAsync($"/api/invoices/{draftInvoiceId}"))).Data!;
        var line = detail.Lines.Single();

        // Round-trip the line with its UNCHANGED TaxId (exactly what the FE always sends) — change-detection
        // must preserve the frozen snapshot without re-resolving, so this 200s instead of the old 400.
        var put = new UpdateInvoiceRequest($"INV-KEEP-{setup.Tag}", detail.InvoiceDate, null, null, null, null,
            new List<UpdateInvoiceLineRequest> { new(line.Id, line.BilledQty, line.TaxId) });
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{draftInvoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "an unchanged TaxId never hits the resolver — the rate-less master cannot block the edit: "
                     + await Body(putResp));

        var updated = (await Read<InvoiceDetailDto>(putResp)).Data!;
        var updatedLine = updated.Lines.Single();
        updatedLine.TaxId.Should().Be(setup.TaxId, because: "the snapshot is preserved untouched");
        updatedLine.TaxCode.Should().Be(setup.TaxCode);
        updatedLine.TaxRatePct.Should().Be(18m, because: "the frozen draft rate survives the master wipe");
        updatedLine.TaxAmount.Should().Be(decimal.Round(updatedLine.LineAmount * 18m / 100m, 2),
            because: "the amount is recomputed from the preserved snapshot rate");
        // Submit still fail-closes on the rate-less master (re-resolve + freeze) — correct and unchanged.
    }

    // ── (fix 6) code-only tax (TaxCode set, TaxId null) survives a PUT round-trip ──────────────────────
    [SkippableFact]
    public async Task Put_round_trip_preserves_a_code_only_line_tax()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, invoiceId, _, storedTaxAmount) = await CreateCodeOnlyDraftAsync("RT");
        var detail = (await Read<InvoiceDetailDto>(await client.GetAsync($"/api/invoices/{invoiceId}"))).Data!;
        var line = detail.Lines.Single();
        line.TaxCode.Should().Be("LEGACY18");
        line.TaxId.Should().BeNull(because: "a manually-keyed ERP tax code has no governed master row");

        // Round-trip: TaxId null + ClearTax false = NO CHANGE — the old contract wiped the code-only tax here.
        var put = new UpdateInvoiceRequest(detail.InvoiceNumber, detail.InvoiceDate, null, null, null, null,
            new List<UpdateInvoiceLineRequest> { new(line.Id, line.BilledQty, null) });
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{invoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));

        var updated = (await Read<InvoiceDetailDto>(putResp)).Data!;
        var updatedLine = updated.Lines.Single();
        updatedLine.TaxCode.Should().Be("LEGACY18", because: "a null TaxId PRESERVES the code-only snapshot");
        updatedLine.TaxAmount.Should().Be(storedTaxAmount,
            because: "a code-only tax has no rate to recompute from — the stored amount is kept as-is");
        updated.TaxAmount.Should().Be(storedTaxAmount, because: "the header re-sums the preserved line amount");
    }

    // ── (fix 6) ClearTax=true is the explicit wipe ──────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Put_with_clear_tax_zeroes_the_code_only_line_tax()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (client, invoiceId, _, _) = await CreateCodeOnlyDraftAsync("CLR");
        var detail = (await Read<InvoiceDetailDto>(await client.GetAsync($"/api/invoices/{invoiceId}"))).Data!;
        var line = detail.Lines.Single();

        var put = new UpdateInvoiceRequest(detail.InvoiceNumber, detail.InvoiceDate, null, null, null, null,
            new List<UpdateInvoiceLineRequest> { new(line.Id, line.BilledQty, null, ClearTax: true) });
        var putResp = await client.PutAsJsonAsync($"/api/invoices/{invoiceId}", put);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(putResp));

        var updated = (await Read<InvoiceDetailDto>(putResp)).Data!;
        var updatedLine = updated.Lines.Single();
        updatedLine.TaxCode.Should().BeNull(because: "ClearTax is the ONLY path that wipes the tax snapshot");
        updatedLine.TaxAmount.Should().Be(0m);
        updated.TaxAmount.Should().Be(0m);
        updated.NetAmount.Should().Be(updated.InvoiceAmount, because: "net = lines + 0 tax after the clear");
    }

    // -------------------- setup --------------------

    /// <summary>
    /// Ships the PO (ASN approve-submit — the auto-generated governed draft is left untouched), then creates a
    /// MANUAL Draft invoice whose single line carries a CODE-ONLY tax (TaxCode set, TaxId null, client-typed
    /// TaxAmount — the legacy/ERP-keyed shape). Returns the manual invoice id + the stored tax amount.
    /// </summary>
    private async Task<(HttpClient Client, Guid InvoiceId, ProcureToPayFlow.Setup Setup, decimal StoredTaxAmount)>
        CreateCodeOnlyDraftAsync(string prefix)
    {
        var (client, _, setup) = await CreateDraftInvoiceAsync();   // ships the full order qty

        var lineAmount = decimal.Round(setup.OrderQty * setup.PriceUnit, 2);
        var taxAmount = 90m;
        var body = new CreateInvoiceRequest(
            setup.PoId, null, $"INV-CODEONLY-{prefix}-{setup.Tag}", DateTime.UtcNow.Date,
            InvoiceAmount: lineAmount, TaxAmount: taxAmount, NetAmount: lineAmount,
            CurrencyCode: "INR", MatchingType: nameof(MatchingType.TwoWay),
            EInvoiceIrn: null, EInvoiceAckNo: null, EWayBillNumber: null, Notes: null,
            Lines: new List<CreateInvoiceLineRequest>
            {
                new(setup.PoLineId, setup.ItemCode, null, setup.OrderQty, setup.PriceUnit, lineAmount,
                    TaxCode: "LEGACY18", TaxAmount: taxAmount),
            });
        var resp = await client.PostAsJsonAsync("/api/invoices", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var invoiceId = (await Read<InvoiceDetailDto>(resp)).Data!.Id;
        return (client, invoiceId, setup, taxAmount);
    }

    /// <summary>Seeds a PO, creates + submits an ASN as the supplier, and returns the auto-created draft invoice id.</summary>
    private async Task<(HttpClient Client, Guid DraftInvoiceId, ProcureToPayFlow.Setup Setup)> CreateDraftInvoiceAsync()
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        // R5 — submit via Send-for-Approval → buyer Approve (the auto-created draft invoice surfaces at submit).
        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, client, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var draftInvoiceId = (await Read<AsnDetailDto>(submitResp)).Data!.DraftInvoiceId!.Value;

        return (client, draftInvoiceId, setup);
    }

    private async Task SetTaxRateAsync(Guid taxId, decimal? rate)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tax = await db.Taxes.IgnoreQueryFilters().FirstAsync(t => t.Id == taxId);
        tax.TaxRate = rate;
        await db.SaveChangesAsync();
    }

    private async Task<decimal> InvoicedToDate(Guid poLineId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == poLineId)
            .Select(l => l.InvoicedQtyToDate).FirstAsync();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
