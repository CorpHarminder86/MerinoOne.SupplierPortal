using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
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
/// R4 — TSD R4 Addendum §8 / D5 / UC-ATT-01..07. End-to-end attachment-requirement governance on REAL SQL, through
/// the real host. Drives the worked example (ASN: TestCertificate=Mandatory, Invoice=Mandatory, PackingSlip=Warning)
/// plus a supplier override proving supplier-wins resolution, asserting submit blocks / confirms / proceeds + audits.
///
/// <para>Each test CLEARS the fixture tenant's policy rows first (the suite shares ONE DB serially) so a prior
/// test's policy never leaks. ASNs are created+submitted as the supplier via the HTTP API; uploads are inserted
/// directly (the present-attachment count is what the evaluator reads).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AttachmentGovernanceTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string AsnEntity = "Asn";
    private const string TestCert = "TestCertificate";
    private const string InvoiceType = "Invoice";
    private const string PackingSlip = "PackingSlip";

    private readonly IntegrationTestFixture _fx;
    public AttachmentGovernanceTests(IntegrationTestFixture fx) => _fx = fx;

    // CRITICAL (suite shares ONE DB serially): a seeded ASN-entity policy is a TENANT DEFAULT (supplierId NULL),
    // so it applies to EVERY supplier in the fixture tenant — a leftover row would block the money-path ASN/Invoice
    // submit tests. Clear the fixture tenant's policy rows BEFORE and AFTER every test so nothing leaks either way.
    public async Task InitializeAsync()
    {
        if (_fx.DbAvailable) await _fx.ClearPoliciesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_fx.DbAvailable) await _fx.ClearPoliciesAsync();
    }

    // ── Worked-example policy on the ASN entity (tenant default). ──────────────────────────────────────
    private async Task SeedWorkedExampleAsync()
    {
        await _fx.ClearPoliciesAsync();
        await _fx.SeedPoliciesAsync(
            new PolicySpec(AsnEntity, TestCert, AttachmentRequirement.Mandatory),
            new PolicySpec(AsnEntity, InvoiceType, AttachmentRequirement.Mandatory),
            new PolicySpec(AsnEntity, PackingSlip, AttachmentRequirement.Warning));
    }

    // Create a Draft ASN (confirmed PO) for a fresh supplier; returns (asnId, supplierId, seccodeId).
    private async Task<(Guid AsnId, Guid SupplierId, Guid SeccodeId, HttpClient Client)> NewDraftAsnAsync()
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var createResp = await client.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup));
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;
        return (asnId, setup.Supplier.SupplierId, setup.Supplier.SeccodeId, client);
    }

    // R5 (§10.3) — the attachment-requirement check MOVED from Submit to Send-for-Approval. A successful pass now
    // leaves the ASN PendingApproval (not Submitted). Mandatory-block / Warning-confirm / Optional-silent behaviour
    // is UNCHANGED — only the firing site moves.
    private async Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid asnId, bool ack = false)
        => await client.PostAsJsonAsync($"/api/asns/{asnId}/send-for-approval", new SendForApprovalRequest(ack));

    // ── UC-ATT-01 — all mandatory present → proceeds ──────────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_ATT_01_all_mandatory_present_proceeds()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedWorkedExampleAsync();
        var (asnId, supplierId, seccodeId, client) = await NewDraftAsnAsync();

        // Both mandatory present; PackingSlip warning also present so no confirm at all.
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, TestCert, seccodeId);
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, InvoiceType, seccodeId);
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, PackingSlip, seccodeId);

        var resp = await SubmitAsync(client, asnId);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var body = await Read<AsnDetailDto>(resp);
        body.Data!.AsnStatus.Should().Be(nameof(AsnStatus.PendingApproval));
        body.ConfirmationRequired.Should().BeFalse();
    }

    // ── UC-ATT-02 — mandatory missing → blocked + named ────────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_ATT_02_mandatory_missing_blocked_and_named()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedWorkedExampleAsync();
        var (asnId, _, seccodeId, client) = await NewDraftAsnAsync();

        // Invoice present, TestCertificate MISSING → blocked, message names Test Certificate.
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, InvoiceType, seccodeId);

        var resp = await SubmitAsync(client, asnId);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        var body = await Read<AsnDetailDto>(resp);
        body.Errors.Should().ContainSingle()
            .Which.Should().Contain("mandatory attachment").And.Contain(TestCert);

        await AssertStillDraft(asnId);
    }

    // ── UC-ATT-03 — warning missing → ConfirmationRequired, then proceed on ack (+ audit) ───────────────
    [SkippableFact]
    public async Task UC_ATT_03_warning_missing_confirms_then_proceeds_and_audits()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedWorkedExampleAsync();
        var (asnId, _, seccodeId, client) = await NewDraftAsnAsync();

        // Both mandatory present; PackingSlip (Warning) MISSING.
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, TestCert, seccodeId);
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, InvoiceType, seccodeId);

        // First submit (no ack) → 200 confirmationRequired, NOT committed, names PackingSlip.
        var first = await SubmitAsync(client, asnId, ack: false);
        first.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(first));
        var firstBody = await Read<AsnDetailDto>(first);
        firstBody.ConfirmationRequired.Should().BeTrue();
        firstBody.Data.Should().BeNull(because: "nothing was committed on the confirm path");
        firstBody.MissingAttachments.Should().Contain(PackingSlip);
        firstBody.Errors.Should().BeEmpty(because: "a Warning confirm is not an error");
        await AssertStillDraft(asnId);

        // Re-submit WITH ack → proceeds; skip audited.
        var second = await SubmitAsync(client, asnId, ack: true);
        second.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(second));
        (await Read<AsnDetailDto>(second)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.PendingApproval));

        (await _fx.CountWarningSkipAuditAsync(AsnEntity, asnId))
            .Should().BeGreaterThanOrEqualTo(1, because: "the acknowledged warning skip is audited (§8.4)");
    }

    // ── UC-ATT-04 — optional missing → silent ──────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_ATT_04_optional_missing_is_silent()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await _fx.ClearPoliciesAsync();
        await _fx.SeedPoliciesAsync(new PolicySpec(AsnEntity, PackingSlip, AttachmentRequirement.Optional));
        var (asnId, _, _, client) = await NewDraftAsnAsync();

        // Optional PackingSlip missing → proceeds silently, no confirm.
        var resp = await SubmitAsync(client, asnId);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var body = await Read<AsnDetailDto>(resp);
        body.ConfirmationRequired.Should().BeFalse();
        body.Data!.AsnStatus.Should().Be(nameof(AsnStatus.PendingApproval));
    }

    // ── UC-ATT-05 — mandatory + warning both missing → mandatory first (no warning prompt yet) ──────────
    [SkippableFact]
    public async Task UC_ATT_05_mandatory_and_warning_missing_mandatory_first()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedWorkedExampleAsync();
        var (asnId, _, seccodeId, client) = await NewDraftAsnAsync();

        // TestCertificate present; Invoice (Mandatory) AND PackingSlip (Warning) both MISSING.
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, TestCert, seccodeId);

        // Even with ack=true, the mandatory block fires FIRST (warning dialog never reached).
        var resp = await SubmitAsync(client, asnId, ack: true);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        var body = await Read<AsnDetailDto>(resp);
        body.Errors.Should().ContainSingle().Which.Should().Contain(InvoiceType);
        body.ConfirmationRequired.Should().BeFalse();
        await AssertStillDraft(asnId);

        // After Invoice is attached, a re-submit (no ack) surfaces the PackingSlip warning (UC-ATT-05 second leg).
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, InvoiceType, seccodeId);
        var resp2 = await SubmitAsync(client, asnId, ack: false);
        resp2.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp2));
        var body2 = await Read<AsnDetailDto>(resp2);
        body2.ConfirmationRequired.Should().BeTrue();
        body2.MissingAttachments.Should().Contain(PackingSlip);
    }

    // ── UC-ATT-06 — no policy configured → never blocks ────────────────────────────────────────────────
    [SkippableFact]
    public async Task UC_ATT_06_no_policy_never_blocks()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await _fx.ClearPoliciesAsync();   // NO policy rows for the ASN entity.
        var (asnId, _, _, client) = await NewDraftAsnAsync();

        var resp = await SubmitAsync(client, asnId);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        (await Read<AsnDetailDto>(resp)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.PendingApproval));
    }

    // ── UC-ATT-07 — policy changed AFTER submit → submitted ASN unaffected ──────────────────────────────
    [SkippableFact]
    public async Task UC_ATT_07_policy_change_after_submit_does_not_affect_submitted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await _fx.ClearPoliciesAsync();   // submit under NO policy.
        var (asnId, _, _, client) = await NewDraftAsnAsync();

        var resp = await SubmitAsync(client, asnId);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        (await Read<AsnDetailDto>(resp)).Data!.AsnStatus.Should().Be(nameof(AsnStatus.PendingApproval));

        // Admin tightens the policy AFTER send-for-approval → the already-PendingApproval ASN is untouched (no retroactivity).
        await SeedWorkedExampleAsync();
        await AssertStatus(asnId, AsnStatus.PendingApproval);
    }

    // ── D5 — supplier override WINS (PackingSlip relaxed to Optional; TestCertificate still Mandatory) ──
    [SkippableFact]
    public async Task D5_supplier_override_wins()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (asnId, supplierId, seccodeId, client) = await NewDraftAsnAsync();

        await _fx.ClearPoliciesAsync();
        // Tenant default: PackingSlip Warning. Supplier override: PackingSlip Optional + TestCertificate Mandatory.
        await _fx.SeedPoliciesAsync(
            new PolicySpec(AsnEntity, PackingSlip, AttachmentRequirement.Warning),
            new PolicySpec(AsnEntity, PackingSlip, AttachmentRequirement.Optional, supplierId),
            new PolicySpec(AsnEntity, TestCert, AttachmentRequirement.Mandatory, supplierId));

        // No uploads: TestCertificate (override Mandatory) blocks; PackingSlip (override Optional) does NOT warn.
        var blocked = await SubmitAsync(client, asnId);
        blocked.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(blocked));
        var blockedBody = await Read<AsnDetailDto>(blocked);
        blockedBody.Errors.Should().ContainSingle().Which.Should().Contain(TestCert);

        // Attach the TestCertificate → submit proceeds with NO warning (PackingSlip is Optional for this supplier).
        await _fx.AddUploadAsync(DocumentOwnerTypeAsn, asnId, TestCert, seccodeId);
        var ok = await SubmitAsync(client, asnId);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(ok));
        var okBody = await Read<AsnDetailDto>(ok);
        okBody.ConfirmationRequired.Should().BeFalse(because: "PackingSlip is Optional for this supplier (override wins)");
        okBody.Data!.AsnStatus.Should().Be(nameof(AsnStatus.PendingApproval));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    private const string DocumentOwnerTypeAsn = "Asn";

    private async Task AssertStillDraft(Guid asnId) => await AssertStatus(asnId, AsnStatus.Draft);

    private async Task AssertStatus(Guid asnId, AsnStatus expected)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var status = await db.Asns.IgnoreQueryFilters().Where(a => a.Id == asnId)
            .Select(a => a.AsnStatus).FirstAsync();
        status.Should().Be(expected);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
