using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R14 — the ASN Confirmation Process (per-supplier buyer confirmation + supplier Post).
///
/// <para>The requirement: when <c>Supplier.AsnConfirmationRequired</c> is Yes (the default), a supplier creates an
/// ASN, adds the lines, and sends it for buyer confirmation <b>without any documents</b>; the buyer confirms; the
/// supplier then uploads the Packing List / Invoice, completes the shipment references, and clicks <b>Post</b> —
/// and the Shipping Date is the POST date, not the confirmation date. When the flag is No there is no approval
/// step at all: the supplier posts straight from Draft.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnConfirmationProcessTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnConfirmationProcessTests(IntegrationTestFixture fx) => _fx = fx;

    // ════════════════════════ Confirmation REQUIRED (the default path) ════════════════════════

    // ── The core requirement: no documents and no shipment references needed to reach the buyer ─────────
    [SkippableFact]
    public async Task Send_for_approval_succeeds_with_no_documents_and_no_shipment_refs()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplier) = await NewDraftAsync();

        // Deliberately NOT stamping invoiceNo / billOfLading / packingList, and attaching nothing.
        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));
        (await Read<AsnDetailDto>(send)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.PendingApproval));
    }

    // ── Confirmation is a decision, not a dispatch ──────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Confirmation_consumes_nothing_and_reopens_the_asn_for_the_supplier()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync(orderQty: 10m);
        await SendAsync(supplier, asnId);
        var detail = await ConfirmAsync(asnId);

        detail.AsnStatus.Should().Be(nameof(AsnStatus.Approved));
        detail.IsLocked.Should().BeFalse(because: "the supplier must be able to add documents and references now");
        detail.CanPost.Should().BeTrue();
        detail.PostedAt.Should().BeNull();
        detail.DraftInvoiceId.Should().BeNull();
        (await ShippedToDate(setup.PoLineId)).Should().Be(0m);
    }

    // ── Post enforces the shipment references (moved off send-for-approval) ─────────────────────────────
    [SkippableFact]
    public async Task Post_without_shipment_refs_is_blocked_and_leaves_the_asn_confirmed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplier) = await NewDraftAsync();
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);

        var post = await ProcureToPayFlow.PostAsync(supplier, asnId);
        post.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(post));
        (await post.Content.ReadAsStringAsync()).Should().Contain("invoice no.");
        await AssertStatus(asnId, AsnStatus.Approved);
    }

    // ── The happy path end-to-end, and the shipping date is the POST date ───────────────────────────────
    [SkippableFact]
    public async Task Full_flow_post_stamps_the_shipping_date_and_reaches_the_erp()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync(orderQty: 10m);
        await SendAsync(supplier, asnId);
        var confirmedAt = (await ConfirmAsync(asnId)).Approval!.DecisionOn!.Value;

        // The supplier completes the shipment AFTER confirmation — exactly the documented sequence.
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);

        var post = await ProcureToPayFlow.PostAsync(supplier, asnId);
        post.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(post));
        var detail = (await Read<AsnDetailDto>(post)).Data!;

        detail.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
        detail.PostedAt.Should().NotBeNull();
        detail.PostedAt!.Value.Should().BeOnOrAfter(confirmedAt,
            because: "BR-07 — the Shipping Date is the Post date, never the approval date");
        detail.PostedBy.Should().Be(SecurityTestHarness.Users.Supplier);
        detail.DraftInvoiceId.Should().NotBeNull();
        (await ShippedToDate(setup.PoLineId)).Should().Be(10m);
    }

    // ── Wrong-state posts ───────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Post_from_draft_or_pending_is_rejected_when_confirmation_is_required()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplier) = await NewDraftAsync();
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);

        (await ProcureToPayFlow.PostAsync(supplier, asnId)).StatusCode
            .Should().Be(HttpStatusCode.Conflict, because: "a Draft cannot be posted when the buyer must confirm first");

        await SendAsync(supplier, asnId);
        (await ProcureToPayFlow.PostAsync(supplier, asnId)).StatusCode
            .Should().Be(HttpStatusCode.Conflict, because: "the buyer has not decided yet");
    }

    // ── Attachments: locked while the buyer reviews, open again once confirmed ──────────────────────────
    [SkippableFact]
    public async Task Attachment_uploads_are_blocked_while_pending_and_allowed_once_confirmed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync();
        await SendAsync(supplier, asnId);

        var whilePending = await UploadAsync(supplier, asnId, setup.Supplier.SupplierId);
        whilePending.Should().BeFalse(because: "the buyer reviews a stable document set");

        await ConfirmAsync(asnId);

        var whenConfirmed = await UploadAsync(supplier, asnId, setup.Supplier.SupplierId);
        whenConfirmed.Should().BeTrue(because: "uploading the Packing List / Invoice after confirmation is the point of R14");
    }

    // ── D5 — the ASN stays confirmed through a post-confirmation edit, and the edit is audited ──────────
    [SkippableFact]
    public async Task Editing_a_confirmed_asn_keeps_it_confirmed_and_writes_an_audit_row()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync(orderQty: 10m);
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);

        var upd = new UpdateAsnRequest(
            DateTime.UtcNow.Date.AddDays(2), null, "Carrier-2", "TRK-2", null, null, null, "edited after confirmation",
            new List<CreateAsnLineRequest> { new(setup.PoLineId, 10m, null, null) },
            InvoiceNo: "INV-1", BillOfLading: "BOL-1", PackingList: "PL-1");
        var put = await supplier.PutAsJsonAsync($"/api/asns/{asnId}", upd);
        put.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(put));

        (await Read<AsnDetailDto>(put)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.Approved),
            because: "an edit must NOT drop a confirmed ASN back to Draft — the buyer's decision still stands");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Set<AuditEntry>().IgnoreQueryFilters()
                .AnyAsync(a => a.EntityId == asnId && a.FieldName == "Edited after buyer confirmation"))
            .Should().BeTrue(because: "post-confirmation edits are recorded (D5 accountability)");
    }

    // ── Cancel is still available on a confirmed-but-unposted ASN ───────────────────────────────────────
    [SkippableFact]
    public async Task Confirmed_asn_can_still_be_cancelled()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplier) = await NewDraftAsync();
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);

        (await supplier.PostAsJsonAsync($"/api/asns/{asnId}/cancel", new { })).StatusCode
            .Should().Be(HttpStatusCode.OK, because: "nothing has been consumed or dispatched yet");
        await AssertStatus(asnId, AsnStatus.Cancelled);
    }

    // ── D8 — the PO stays locked while an ASN is confirmed-but-unposted ─────────────────────────────────
    [SkippableFact]
    public async Task Second_asn_on_the_same_po_is_blocked_while_the_first_is_confirmed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (firstId, setup, supplier) = await NewDraftAsync(orderQty: 100m, shippedQty: 10m);
        await SendAsync(supplier, firstId);
        await ConfirmAsync(firstId);   // confirmed, NOT posted — balance still unconsumed

        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 10m));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var secondId = (await Read<AsnDetailDto>(create)).Data!.Id;
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, secondId);

        var send = await supplier.PostAsJsonAsync($"/api/asns/{secondId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the first ASN still holds an unconsumed claim on the PO until it posts (D8)");
        (await send.Content.ReadAsStringAsync()).Should().Contain("awaiting post");
    }

    // ════════════════════════ Confirmation NOT required ════════════════════════

    [SkippableFact]
    public async Task No_confirmation_supplier_posts_straight_from_draft_and_cannot_send_for_approval()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync(orderQty: 10m);
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, setup.Supplier.SupplierId, required: false);
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);

        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "this supplier has no approval step; the ASN must be posted directly");
        (await send.Content.ReadAsStringAsync()).Should().Contain("does not require buyer confirmation");

        var post = await ProcureToPayFlow.PostAsync(supplier, asnId);
        post.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(post));
        var detail = (await Read<AsnDetailDto>(post)).Data!;
        detail.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
        detail.PostedAt.Should().NotBeNull();
        detail.Approval.Should().BeNull(because: "no approval session is ever created in this mode");
        (await ShippedToDate(setup.PoLineId)).Should().Be(10m);
    }

    // ── Flipping the flag must never strand a confirmed ASN ─────────────────────────────────────────────
    [SkippableFact]
    public async Task Flipping_to_no_confirmation_still_lets_an_already_confirmed_asn_post()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync(orderQty: 10m);
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);

        // Admin switches the supplier to No-confirmation while this ASN sits in Approved.
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, setup.Supplier.SupplierId, required: false);
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);

        (await ProcureToPayFlow.PostAsync(supplier, asnId)).StatusCode
            .Should().Be(HttpStatusCode.OK, because: "Approved is postable in BOTH modes, so the flip strands nothing");
    }

    // ════════════════════════ §8.1 — the PO gate override survives the move to Post ════════════════════

    [SkippableFact]
    public async Task Gate_blocked_post_is_refused_for_the_supplier_but_an_admin_can_override_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, setup, supplier) = await NewDraftAsync(orderQty: 10m);
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);

        // An ERP Modify re-releases the PO after the buyer confirmed — the submit-time gate now blocks.
        await SetPoStatusAsync(setup.PoId, PoStatus.Released);

        var blocked = await ProcureToPayFlow.PostAsync(supplier, asnId);
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(blocked));
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Accept");

        // The supplier holds no PurchaseOrder.OverrideGate, so a reason changes nothing for them.
        (await ProcureToPayFlow.PostAsync(supplier, asnId, overrideReason: "please ship")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, because: "the supplier cannot override the gate");

        // An Admin holds BOTH Asn.Post and PurchaseOrder.OverrideGate — the §6.5 escape hatch still works.
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var overridden = await ProcureToPayFlow.PostAsync(admin, asnId, overrideReason: "expedited, PO re-release pending");
        overridden.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(overridden));
        await AssertStatus(asnId, AsnStatus.Submitted);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Set<AuditEntry>().IgnoreQueryFilters()
                .AnyAsync(a => a.EntityId == setup.PoId && a.FieldName!.StartsWith("Gate override")))
            .Should().BeTrue(because: "the override is audited against the PO (§6.5)");
    }

    // ── A Buyer holds Asn.Approve but NOT Asn.Post ──────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Buyer_cannot_post()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (asnId, _, supplier) = await NewDraftAsync();
        await SendAsync(supplier, asnId);
        await ConfirmAsync(asnId);
        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asnId);

        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        (await ProcureToPayFlow.PostAsync(buyer, asnId)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, because: "posting is the supplier's action; Asn.Approve does not grant it");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid AsnId, ProcureToPayFlow.Setup Setup, HttpClient Supplier)> NewDraftAsync(
        decimal orderQty = 10m, decimal? shippedQty = null)
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: orderQty);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var create = await supplier.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        return ((await Read<AsnDetailDto>(create)).Data!.Id, setup, supplier);
    }

    private async Task SendAsync(HttpClient supplier, Guid asnId)
    {
        var send = await supplier.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));
    }

    private async Task<AsnDetailDto> ConfirmAsync(Guid asnId)
    {
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));
        return (await Read<AsnDetailDto>(approve)).Data!;
    }

    /// <summary>Attempts a real multipart upload against the ASN; true only when the API actually accepted it.</summary>
    private static async Task<bool> UploadAsync(HttpClient client, Guid asnId, Guid supplierId)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "packing-list.pdf");
        content.Add(new StringContent("Asn"), "ownerEntityType");
        content.Add(new StringContent(asnId.ToString()), "ownerEntityId");
        content.Add(new StringContent(supplierId.ToString()), "supplierId");
        content.Add(new StringContent("PackingSlip"), "documentType");

        var resp = await client.PostAsync("/api/document-uploads/attach", content);
        if (resp.StatusCode != HttpStatusCode.OK) return false;
        // Result<T>.Fail is a 200 with success=false, so the flag — not the status — is what decides.
        var body = await Read<DocumentAttachmentDto>(resp);
        return body.Success;
    }

    private async Task<decimal> ShippedToDate(Guid poLineId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == poLineId)
            .Select(l => l.ShippedQtyToDate).FirstAsync();
    }

    private async Task SetPoStatusAsync(Guid poId, PoStatus status)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == poId);
        po.PoStatus = status;
        await db.SaveChangesAsync();
    }

    private async Task AssertStatus(Guid asnId, AsnStatus expected)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Asns.IgnoreQueryFilters().Where(a => a.Id == asnId)
            .Select(a => a.AsnStatus).FirstAsync()).Should().Be(expected);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
