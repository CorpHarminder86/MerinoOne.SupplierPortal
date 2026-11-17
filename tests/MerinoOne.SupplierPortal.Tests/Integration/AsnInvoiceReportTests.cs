using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Documents;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static MerinoOne.SupplierPortal.Tests.Infrastructure.AttachmentGovernanceHarness;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// 2026-11-17 feedback — the ASN's Supplier Invoice report BEFORE Post (GET api/asns/{id}/invoice-report[/groups], POST .../download),
/// plus the end-to-end Trusted-Supplier flow (ASN Confirmation Required = No).
///
/// <para>Each test seeds its own tagged supplier / PO / ASN and clears the tenant policies first (the established harness call), so a
/// leftover policy from another suite can neither block the Post nor enable a slot. The confirmation flag is flipped only on the
/// fresh supplier, so no seeded row a sibling test relies on is changed.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnInvoiceReportTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnInvoiceReportTests(IntegrationTestFixture fx) => _fx = fx;

    // ════════════════════ Confirmation REQUIRED: available from Approved, matches the draft Post creates ════════════════════

    [SkippableFact]
    public async Task Required_mode_report_is_unavailable_until_approved_then_matches_the_draft_invoice_post_creates()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplier) = await NewDraftAsync();

        (await supplier.GetAsync(GroupsUrl(asnId))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, because: "a Draft of a confirmation-required supplier has no invoice yet");

        await SendAsync(supplier, asnId);
        (await supplier.GetAsync(GroupsUrl(asnId))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, because: "PendingApproval never prints");

        await ConfirmAsync(asnId);
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);   // stamps Invoice No. INV-TEST-1

        var groups = await GroupsAsync(supplier, asnId);
        groups.Should().ContainSingle();
        groups[0].Group.Should().Be(1);
        groups[0].InvoiceNumber.Should().Be("INV-TEST-1", because: "the supplier's own Invoice No. is the invoice number");
        groups[0].Currency.Should().Be("INR");
        groups[0].GrandTotal.Should().Be(1180m, because: "10 x 100 = 1000 taxable + 18% tax");

        var preview = await supplier.GetAsync(PreviewUrl(asnId));
        preview.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(preview));
        preview.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        IsPdf(await preview.Content.ReadAsByteArrayAsync()).Should().BeTrue();

        // Post creates the real draft — same lines, same total as the report the supplier attached.
        var post = await ProcureToPayFlow.PostAsync(supplier, asnId);
        post.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(post));
        var draftId = (await Read<AsnDetailDto>(post)).Data!.DraftInvoiceId!.Value;

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var draft = await db.Invoices.IgnoreQueryFilters().Include(i => i.Lines).SingleAsync(i => i.Id == draftId);
        draft.NetAmount.Should().Be(groups[0].GrandTotal);
        draft.Lines.Single().BilledQty.Should().Be(10m);
    }

    [SkippableFact]
    public async Task Report_applies_the_po_line_discount_exactly_like_the_draft()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplier) = await NewDraftAsync(discountPct: 10m);
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);

        var groups = await GroupsAsync(supplier, asnId);
        groups.Single().GrandTotal.Should().Be(1062m, because: "(1000 - 10% = 900) + 18% tax = 1062");
    }

    [SkippableFact]
    public async Task Report_is_refused_with_the_tax_reason_when_a_tax_code_has_no_rate()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplier) = await NewDraftAsync(taxRate: null);
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);

        var resp = await supplier.GetAsync(GroupsUrl(asnId));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        (await Body(resp)).Should().Contain("without a usable rate");
    }

    [SkippableFact]
    public async Task Supplier_download_attaches_the_report_to_the_asn_and_an_unknown_group_is_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync();
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);
        await EnableSlotAsync(DocumentOwnerTypes.Asn, GeneratedReportTypes.EInvoice, setup.Supplier.SupplierId);

        (await supplier.PostAsync($"{PreviewUrl(asnId)}/download?group=2", null)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, because: "the ASN has a single group");

        var download = await supplier.PostAsync($"{PreviewUrl(asnId)}/download", null);
        download.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(download));
        Attached(download).Should().Be("true");

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var docs = await db.DocumentUploads.IgnoreQueryFilters()
            .Where(d => d.OwnerEntityType == DocumentOwnerTypes.Asn && d.OwnerEntityId == asnId && !d.IsDeleted)
            .ToListAsync();
        docs.Should().ContainSingle(d => d.IsSystemGenerated && d.DocumentType == GeneratedReportTypes.EInvoice);
    }

    [SkippableFact]
    public async Task A_report_printed_before_the_invoice_no_is_replaced_not_kept_when_reprinted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync();
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);
        await EnableSlotAsync(DocumentOwnerTypes.Asn, GeneratedReportTypes.EInvoice, setup.Supplier.SupplierId);

        Attached(await supplier.PostAsync($"{PreviewUrl(asnId)}/download", null)).Should().Be("true");   // DRAFT-… name
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);                                      // Invoice No. set
        Attached(await supplier.PostAsync($"{PreviewUrl(asnId)}/download", null)).Should().Be("true");   // INV-TEST-1 name

        var names = await GeneratedInvoiceReportNamesAsync(asnId);
        names.Should().Equal(new[] { "Invoice-INV-TEST-1.pdf" },
            because: "the report printed under the draft placeholder is stale and must not linger beside the current one");
    }

    [SkippableFact]
    public async Task Multi_currency_asn_prints_one_report_per_group_keeps_both_attachments_and_matches_the_drafts()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        await _fx.ClearPoliciesAsync();
        var inr = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m);
        var usd = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, inr.Supplier, orderQty: 5m, priceUnit: 20m,
            currencyCode: "USD", confirm: true);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, inr.PoId);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, usd.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var req = ProcureToPayFlow.SimpleAsn(inr) with
        {
            PurchaseOrderId = null,
            PurchaseOrderIds = new List<Guid> { inr.PoId, usd.PoId },
            Lines = new List<CreateAsnLineRequest>
            {
                new(inr.PoLineId, ShippedQty: 10m, BatchNumber: null, ExpiryDate: null),
                new(usd.PoLineId, ShippedQty: 5m, BatchNumber: null, ExpiryDate: null),
            },
        };
        var create = await supplier.PostAsJsonAsync("/api/asns", req);
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, inr.Supplier.SupplierId, required: false);
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);
        await EnableSlotAsync(DocumentOwnerTypes.Asn, GeneratedReportTypes.EInvoice, inr.Supplier.SupplierId);

        var groups = await GroupsAsync(supplier, asnId);
        groups.Select(g => (g.Group, g.InvoiceNumber, g.Currency)).Should().Equal(
            (1, "INV-TEST-1-1", "INR"),
            (2, "INV-TEST-1-2", "USD"));
        groups[0].GrandTotal.Should().Be(1180m);
        groups[1].GrandTotal.Should().Be(118m, because: "5 x 20 = 100 + 18%");

        (await supplier.GetAsync($"{PreviewUrl(asnId)}?group=2")).StatusCode.Should().Be(HttpStatusCode.OK);
        Attached(await supplier.PostAsync($"{PreviewUrl(asnId)}/download?group=1", null)).Should().Be("true");
        Attached(await supplier.PostAsync($"{PreviewUrl(asnId)}/download?group=2", null)).Should().Be("true");
        (await GeneratedInvoiceReportNamesAsync(asnId)).Should().BeEquivalentTo(
            new[] { "Invoice-INV-TEST-1-1.pdf", "Invoice-INV-TEST-1-2.pdf" },
            because: "each group keeps its own attachment");

        var post = await ProcureToPayFlow.PostAsync(supplier, asnId);
        post.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(post));

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var drafts = await db.Invoices.IgnoreQueryFilters().Where(i => i.AsnId == asnId && !i.IsDeleted)
            .OrderBy(i => i.InvoiceNumber).Select(i => new { i.CurrencyCode, i.NetAmount }).ToListAsync();
        drafts.Select(d => (d.CurrencyCode, d.NetAmount)).Should().Equal(
            groups.Select(g => (g.Currency, g.GrandTotal)),
            because: "the drafts Post creates come in the same groups, order and totals as the reports");
    }

    // ════════════════════ Trusted supplier (ASN Confirmation Required = No) — the whole flow ════════════════════

    [SkippableFact]
    public async Task Trusted_supplier_prints_from_draft_posts_without_any_buyer_step_and_gets_the_matching_draft_invoice()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync();
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, setup.Supplier.SupplierId, required: false);

        // Draft: the detail offers Post (not Send for approval), and both reports print straight away.
        var draft = await DetailAsync(supplier, asnId);
        draft.AsnStatus.Should().Be(nameof(AsnStatus.Draft));
        draft.AsnConfirmationRequired.Should().BeFalse();
        draft.CanPost.Should().BeTrue();
        (await supplier.GetAsync($"/api/asns/{asnId}/packing-list")).StatusCode.Should().Be(HttpStatusCode.OK);

        var before = await GroupsAsync(supplier, asnId);
        before.Single().InvoiceNumber.Should().Be($"DRAFT-{draft.AsnNumber}-1",
            because: "with no Invoice No. entered yet the report carries the draft placeholder Post will use");

        // There is no approval step at all.
        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Post still enforces the shipment references.
        var early = await ProcureToPayFlow.PostAsync(supplier, asnId);
        early.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(early));
        (await Body(early)).Should().Contain("invoice no.");

        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);
        var withRef = await GroupsAsync(supplier, asnId);
        withRef.Single().InvoiceNumber.Should().Be("INV-TEST-1");

        var post = await ProcureToPayFlow.PostAsync(supplier, asnId);
        post.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(post));
        var posted = (await Read<AsnDetailDto>(post)).Data!;
        posted.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
        posted.PostedAt.Should().NotBeNull();
        posted.Approval.Should().BeNull();
        posted.DraftInvoiceId.Should().NotBeNull();

        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.AsnApprovals.IgnoreQueryFilters().AnyAsync(a => a.AsnId == asnId))
            .Should().BeFalse(because: "no approval session is ever opened for a trusted supplier");
        var inv = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == posted.DraftInvoiceId);
        inv.NetAmount.Should().Be(withRef.Single().GrandTotal);
        (await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == setup.PoLineId)
                .Select(l => l.ShippedQtyToDate).SingleAsync())
            .Should().Be(10m, because: "balance is consumed at Post");
    }

    [SkippableFact]
    public async Task Buyer_cannot_approve_a_trusted_suppliers_draft()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, _) = await NewDraftAsync();
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, setup.Supplier.SupplierId, required: false);

        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().NotBe(HttpStatusCode.OK, because: "only a PendingApproval ASN can be approved");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static string GroupsUrl(Guid asnId) => $"/api/asns/{asnId}/invoice-report/groups";
    private static string PreviewUrl(Guid asnId) => $"/api/asns/{asnId}/invoice-report";

    private async Task<(Guid AsnId, ProcureToPayFlow.Setup Setup, HttpClient Supplier)> NewDraftAsync(
        decimal? taxRate = 18m, decimal discountPct = 0m)
    {
        await _fx.ClearPoliciesAsync();
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m, taxRate: taxRate, discountPct: discountPct);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        return ((await Read<AsnDetailDto>(create)).Data!.Id, setup, supplier);
    }

    private static async Task SendAsync(HttpClient supplier, Guid asnId)
    {
        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));
    }

    private async Task ConfirmAsync(Guid asnId)
    {
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));
    }

    private static async Task<AsnDetailDto> DetailAsync(HttpClient client, Guid asnId)
    {
        var resp = await client.GetAsync($"/api/asns/{asnId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<AsnDetailDto>(resp)).Data!;
    }

    private static async Task<List<AsnInvoiceReportRefDto>> GroupsAsync(HttpClient client, Guid asnId)
    {
        var resp = await client.GetAsync(GroupsUrl(asnId));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var result = await Read<List<AsnInvoiceReportRefDto>>(resp);
        result.Success.Should().BeTrue(because: string.Join("; ", result.Errors));
        return result.Data!;
    }

    /// <summary>Same as ReportDownloadAttachTests: a SUPPLIER-OVERRIDE policy for the fresh supplier; skips rather than flip a shared inactive master.</summary>
    private async Task EnableSlotAsync(string entityCode, string typeCode, Guid supplierId)
    {
        await using (var scope = _fx.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var type = await db.AttachmentTypes.IgnoreQueryFilters()
                .Where(t => t.TenantId == IntegrationTestFixture.TenantId && t.Code == typeCode && !t.IsDeleted)
                .Select(t => (bool?)t.IsActive).FirstOrDefaultAsync();
            var entity = await db.AttachmentEntities.IgnoreQueryFilters()
                .Where(e => e.TenantId == IntegrationTestFixture.TenantId && e.Code == entityCode && !e.IsDeleted)
                .Select(e => (bool?)e.IsActive).FirstOrDefaultAsync();
            Skip.If(type == false, $"the shared attachment type '{typeCode}' is inactive in the test tenant");
            Skip.If(entity == false, $"the shared attachment entity '{entityCode}' is inactive in the test tenant");
        }

        await _fx.SeedPoliciesAsync(new PolicySpec(entityCode, typeCode, AttachmentRequirement.Optional, supplierId));
    }

    private async Task<List<string>> GeneratedInvoiceReportNamesAsync(Guid asnId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DocumentUploads.IgnoreQueryFilters()
            .Where(d => d.OwnerEntityType == DocumentOwnerTypes.Asn && d.OwnerEntityId == asnId && !d.IsDeleted
                        && d.IsSystemGenerated && d.DocumentType == GeneratedReportTypes.EInvoice)
            .Select(d => d.FileName).ToListAsync();
    }

    private static bool IsPdf(byte[] bytes) => bytes.Length > 1000 && Encoding.ASCII.GetString(bytes, 0, 4) == "%PDF";

    private static string Attached(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("X-Report-Attached", out var values) ? values.Single() : "<missing>";

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
