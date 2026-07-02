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
/// R6 — the GROUPED draft-invoice generation at ASN approve→submit (supersedes the R4 one-invoice-per-ASN model):
/// one Draft invoice per PO (currency, payment-term) group numbered <c>DRAFT-{asnNumber}-{n}</c>, per-line
/// billedQty = live shipped − invoiced; whole-ASN tax gate (null PO-line tax rate ⇒ Blocked, no invoice, ASN still
/// Submitted, buyer notified) and the <c>POST /api/invoices/from-asn</c> Retry that clears the block. Verified
/// through the REAL host on real SQL.
///
/// <para>Money path: scope gate OFF; fresh tagged supplier/PO per test.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnDraftInvoiceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnDraftInvoiceTests(IntegrationTestFixture fx) => _fx = fx;

    // ── single-currency single-PO ASN ⇒ exactly one Draft, DRAFT-{asn}-1, origin AsnGenerated ─────────
    [SkippableFact]
    public async Task Submitting_an_asn_auto_creates_exactly_one_draft_invoice()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = ProcureToPayFlow.SimpleAsn(setup);
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        // R5 — submit through Send-for-Approval → buyer Approve (the draft invoice is created at the submit step).
        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = await Read<AsnDetailDto>(submitResp);
        submitted.Data!.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
        submitted.Data!.DraftInvoiceId.Should().NotBeNull(because: "ASN submit auto-creates the draft invoice");
        submitted.Data!.DraftInvoiceIds.Should().ContainSingle(because: "a single-currency single-PO ASN is one group");
        submitted.Data!.InvoiceGenerationStatus.Should().Be("Generated");
        submitted.Data!.InvoiceGenerationNote.Should().BeNull();

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var invoices = await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.AsnId == asnId && !i.IsDeleted)
            .ToListAsync();
        invoices.Should().HaveCount(1, because: "one (currency, payment-term) group ⇒ one draft invoice");
        invoices[0].InvoiceStatus.Should().Be(InvoiceStatus.Draft);
        invoices[0].InvoiceOrigin.Should().Be(InvoiceOrigin.AsnGenerated);
        invoices[0].PurchaseOrderId.Should().Be(setup.PoId, because: "a single-PO group sets the scalar header PO");
        invoices[0].Id.Should().Be(submitted.Data!.DraftInvoiceId!.Value);
        invoices[0].InvoiceNumber.Should().StartWith("DRAFT-").And.EndWith("-1",
            because: "R6 provisional numbers are DRAFT-{asnNumber}-{groupSeq}");

        // Money math: billed = live shipped − invoiced (= full order), taxed at the seeded 18%.
        var line = await db.InvoiceLines.IgnoreQueryFilters().SingleAsync(l => l.InvoiceId == invoices[0].Id);
        line.BilledQty.Should().Be(setup.OrderQty);
        line.TaxRatePct.Should().Be(18m);
        line.TaxId.Should().Be(setup.TaxId!.Value);
        line.LineAmount.Should().Be(decimal.Round(setup.OrderQty * setup.PriceUnit, 2));
        line.TaxAmount.Should().Be(decimal.Round(line.LineAmount * 18m / 100m, 2));
        invoices[0].NetAmount.Should().Be(line.LineAmount + line.TaxAmount, because: "net = lines + tax");
    }

    // ── (a) mixed-currency 2-PO ASN ⇒ 2 drafts, grouped + numbered DRAFT-{asn}-1 / -2 ──────────────────
    [SkippableFact]
    public async Task Mixed_currency_two_po_asn_generates_one_draft_per_currency_group()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // ONE supplier, TWO Accepted POs in different currencies (INR + USD), same ship-to.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var poInr = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, currencyCode: "INR", confirm: true);
        var poUsd = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, supplier, currencyCode: "USD", confirm: true);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, poInr.PoId);

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var create = new CreateAsnRequest(
            PurchaseOrderId: null, PurchaseOrderIds: new[] { poInr.PoId, poUsd.PoId },
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1),
            TimeWindow: null, CarrierName: "Carrier", TrackingNumber: "TRK",
            VehicleNumber: null, DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(poInr.PoLineId, ShippedQty: poInr.OrderQty, BatchNumber: null, ExpiryDate: null),
                new(poUsd.PoLineId, ShippedQty: poUsd.OrderQty, BatchNumber: null, ExpiryDate: null),
            });
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asn = (await Read<AsnDetailDto>(createResp)).Data!;

        // R16 (single-currency guard) was REMOVED (plan D4) — the mixed-currency ASN submits fine.
        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asn.Id);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = (await Read<AsnDetailDto>(submitResp)).Data!;
        submitted.InvoiceGenerationStatus.Should().Be("Generated");
        submitted.DraftInvoiceIds.Should().HaveCount(2, because: "two (currency, payment-term) groups ⇒ two drafts");
        submitted.DraftInvoiceId.Should().Be(submitted.DraftInvoiceIds![0], because: "the scalar stays = first (back-compat)");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoices = await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.AsnId == asn.Id && !i.IsDeleted)
            .OrderBy(i => i.InvoiceNumber)
            .ToListAsync();

        invoices.Should().HaveCount(2);
        invoices[0].InvoiceNumber.Should().Be($"DRAFT-{asn.AsnNumber}-1");
        invoices[1].InvoiceNumber.Should().Be($"DRAFT-{asn.AsnNumber}-2");
        // Groups are ordered by currency code — INR before USD.
        invoices[0].CurrencyCode.Should().Be("INR");
        invoices[1].CurrencyCode.Should().Be("USD");
        invoices[0].PurchaseOrderId.Should().Be(poInr.PoId, because: "each single-PO group keeps its scalar header PO");
        invoices[1].PurchaseOrderId.Should().Be(poUsd.PoId);
        invoices.Should().OnlyContain(i => i.InvoiceOrigin == InvoiceOrigin.AsnGenerated
                                           && i.InvoiceStatus == InvoiceStatus.Draft);
    }

    // ── (b) null-rate tax ⇒ Blocked: no invoice, note set, ASN still Submitted, buyer e-mailed ─────────
    // ── (c) fix the rate then POST /api/invoices/from-asn ⇒ Generated + drafts exist ───────────────────
    [SkippableFact]
    public async Task Null_rate_tax_blocks_generation_then_retry_after_fix_generates()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // The PO line's tax resolves but has NO rate ⇒ the whole-ASN tax gate blocks generation.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, taxRate: null);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a Blocked generation must NOT abort the ASN approval: " + await Body(submitResp));
        var submitted = (await Read<AsnDetailDto>(submitResp)).Data!;

        submitted.AsnStatus.Should().Be(nameof(AsnStatus.Submitted), because: "the approve/submit still commits");
        submitted.InvoiceGenerationStatus.Should().Be("Blocked");
        submitted.InvoiceGenerationNote.Should().Contain(setup.TaxCode!,
            because: "the note names the offending tax code");
        submitted.DraftInvoiceId.Should().BeNull();
        submitted.DraftInvoiceIds.Should().BeEmpty();

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Invoices.IgnoreQueryFilters().CountAsync(i => i.AsnId == asnId && !i.IsDeleted))
                .Should().Be(0, because: "a blocked generation creates ZERO invoices");

            // The mapped buyer got the blocked notification (EmailOutbox row staged in the approve transaction).
            var mail = await db.EmailOutbox.IgnoreQueryFilters()
                .Where(m => m.Subject == $"Invoice generation blocked for ASN {submitted.AsnNumber}")
                .ToListAsync();
            mail.Should().NotBeEmpty(because: "the mapped buyer users are notified of the block");
            mail.Should().Contain(m => m.ToEmail == "sec-buyer-a@merino.local");
        }

        // ---- (c) fix the tax rate, then Retry via POST /api/invoices/from-asn --------------------------
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tax = await db.Taxes.IgnoreQueryFilters().FirstAsync(t => t.Id == setup.TaxId!.Value);
            tax.TaxRate = 18m;
            await db.SaveChangesAsync();
        }

        var retryResp = await supplierClient.PostAsJsonAsync("/api/invoices/from-asn",
            new CreateInvoiceFromAsnRequest(asnId));
        retryResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(retryResp));
        var draft = (await Read<InvoiceDetailDto>(retryResp)).Data!;
        draft.InvoiceStatus.Should().Be(nameof(InvoiceStatus.Draft));
        draft.InvoiceOrigin.Should().Be(nameof(InvoiceOrigin.AsnGenerated));
        draft.Lines.Should().ContainSingle().Which.TaxRatePct.Should().Be(18m);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var asn = await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId);
            asn.InvoiceGenerationStatus.Should().Be("Generated", because: "a successful retry clears the block");
            asn.InvoiceGenerationNote.Should().BeNull();
            (await db.Invoices.IgnoreQueryFilters().CountAsync(i => i.AsnId == asnId && !i.IsDeleted))
                .Should().Be(1);
        }

        // Idempotency unchanged: a second retry returns the existing draft, never a second insert.
        var retryAgain = await supplierClient.PostAsJsonAsync("/api/invoices/from-asn",
            new CreateInvoiceFromAsnRequest(asnId));
        retryAgain.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(retryAgain));
        (await Read<InvoiceDetailDto>(retryAgain)).Data!.Id.Should().Be(draft.Id);
    }

    // ── (fix 7) idempotent retry path reconciles a stale Blocked flag when invoices already exist ──────
    [SkippableFact]
    public async Task Retry_on_blocked_asn_with_existing_invoice_flips_blocked_to_generated()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (supplierClient, asnId, asnNumber, setup) = await CreateBlockedAsnAsync();

        // While generation is Blocked, the supplier keys a MANUAL invoice against the ASN (legitimate — the
        // generator's tax gate does not stop manual entry). The Blocked flag is now stale.
        var lineAmount = decimal.Round(setup.OrderQty * setup.PriceUnit, 2);
        var manual = new CreateInvoiceRequest(
            setup.PoId, asnId, $"INV-MANBLK-{setup.Tag}", DateTime.UtcNow.Date,
            InvoiceAmount: lineAmount, TaxAmount: 0m, NetAmount: lineAmount,
            CurrencyCode: "INR", MatchingType: nameof(MatchingType.TwoWay),
            EInvoiceIrn: null, EInvoiceAckNo: null, EWayBillNumber: null, Notes: null,
            Lines: new List<CreateInvoiceLineRequest>
            {
                new(setup.PoLineId, setup.ItemCode, null, setup.OrderQty, setup.PriceUnit, lineAmount, null, 0m),
            });
        var createResp = await supplierClient.PostAsJsonAsync("/api/invoices", manual);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var manualId = (await Read<InvoiceDetailDto>(createResp)).Data!.Id;

        // Retry hits the factory's IDEMPOTENT early-return (invoices exist) — it must reconcile the stale flag.
        var retryResp = await supplierClient.PostAsJsonAsync("/api/invoices/from-asn",
            new CreateInvoiceFromAsnRequest(asnId));
        retryResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(retryResp));
        (await Read<InvoiceDetailDto>(retryResp)).Data!.Id.Should().Be(manualId,
            because: "the idempotent path returns the existing invoice, never a second insert");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asn = await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId);
        asn.InvoiceGenerationStatus.Should().Be("Generated",
            because: "invoices exist, so a stale Blocked flag must reconcile to Generated on the idempotent path");
        asn.InvoiceGenerationNote.Should().BeNull();
        asnNumber.Should().NotBeNullOrEmpty();
    }

    // ── (fix 8) a still-blocked retry with the same reason does NOT stage duplicate buyer e-mails ──────
    [SkippableFact]
    public async Task Second_still_blocked_retry_does_not_add_a_second_email_outbox_row()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (supplierClient, asnId, asnNumber, _) = await CreateBlockedAsnAsync();

        var baseline = await BlockedMailCountAsync(asnNumber);
        baseline.Should().BeGreaterThanOrEqualTo(1, because: "the FIRST block staged the buyer notification");

        // Nothing was fixed — the retry re-blocks with the SAME note ⇒ 400 and NO new EmailOutbox rows.
        var retryResp = await supplierClient.PostAsJsonAsync("/api/invoices/from-asn",
            new CreateInvoiceFromAsnRequest(asnId));
        retryResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the tax gate still blocks: " + await Body(retryResp));

        (await BlockedMailCountAsync(asnNumber)).Should().Be(baseline,
            because: "an unchanged Blocked state must not spam a duplicate notification per retry");
    }

    // -------------------- helpers --------------------

    /// <summary>NULL-rate tax PO → ASN → approve-submit ⇒ ASN Submitted with InvoiceGenerationStatus=Blocked.</summary>
    private async Task<(HttpClient SupplierClient, Guid AsnId, string AsnNumber, ProcureToPayFlow.Setup Setup)>
        CreateBlockedAsnAsync()
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, taxRate: null);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = (await Read<AsnDetailDto>(submitResp)).Data!;
        submitted.InvoiceGenerationStatus.Should().Be("Blocked");
        return (supplierClient, asnId, submitted.AsnNumber, setup);
    }

    private async Task<int> BlockedMailCountAsync(string asnNumber)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.EmailOutbox.IgnoreQueryFilters()
            .CountAsync(m => m.Subject == $"Invoice generation blocked for ASN {asnNumber}");
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
