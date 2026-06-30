using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Documents;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Xunit;
using static MerinoOne.SupplierPortal.Tests.Infrastructure.AttachmentGovernanceHarness;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R5 — TSD R5 Addendum §13 / §13.8 / UC-ATTUI-01..03 + §13.7. The policy-driven Attachment Panel READ-MODEL
/// (<c>GET /api/attachments/panel</c>) on REAL SQL through the real host. Drives the worked example
/// (ASN: TestCertificate=Mandatory, PackingSlip=Optional), the no-policy → empty case, multiple-files-per-slot,
/// and entity-agnostic reuse on the Invoice entity — the SAME query, no new UI.
///
/// <para>Each test CLEARS the fixture tenant's policy rows first (the suite shares ONE DB serially) so a prior
/// test's tenant-default policy never leaks. Uploads are inserted directly (the read-model projects them);
/// the panel is purely descriptive and enforces nothing.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AttachmentPanelTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string AsnEntity = "Asn";
    private const string InvoiceEntity = "Invoice";
    private const string TestCert = "TestCertificate";
    private const string PackingSlip = "PackingSlip";

    private readonly IntegrationTestFixture _fx;
    public AttachmentPanelTests(IntegrationTestFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        if (_fx.DbAvailable) await _fx.ClearPoliciesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_fx.DbAvailable) await _fx.ClearPoliciesAsync();
    }

    // ── UC-ATTUI-01 — slots generated from policy, with requirement badges + §13.3 ordering ─────────────
    [SkippableFact]
    public async Task UC_ATTUI_01_slots_from_policy_with_badges()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await _fx.SeedPoliciesAsync(
            new PolicySpec(AsnEntity, TestCert, AttachmentRequirement.Mandatory),
            new PolicySpec(AsnEntity, PackingSlip, AttachmentRequirement.Optional));

        // Slots are policy-driven (not document-driven); a fresh owner id keeps the assertion independent of any
        // prior test's uploads on the shared seeded AsnId.
        var slots = await PanelAsync(AsnEntity, Guid.NewGuid());

        slots.Should().HaveCount(2);
        // §13.3 — Mandatory before Optional (the Mandatory TestCertificate sorts first regardless of name).
        slots[0].TypeCode.Should().Be(TestCert);
        slots[0].Requirement.Should().Be(nameof(AttachmentRequirement.Mandatory));
        slots[1].TypeCode.Should().Be(PackingSlip);
        slots[1].Requirement.Should().Be(nameof(AttachmentRequirement.Optional));
        // Each slot carries a (possibly empty) file array — no uploads yet.
        slots.Should().OnlyContain(s => s.Documents != null);
    }

    // ── UC-ATTUI-02 — no policy → empty list (host renders no panel) ────────────────────────────────────
    [SkippableFact]
    public async Task UC_ATTUI_02_no_policy_returns_empty_list()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // No policy rows for the ASN entity (cleared in InitializeAsync).
        var slots = await PanelAsync(AsnEntity, IntegrationTestFixture.AsnId);
        slots.Should().BeEmpty(because: "no active policy ⇒ no panel (the 'no policy → no control' rule, §13.2)");
    }

    // ── UC-ATTUI-03 — multiple files per slot → all returned (§13.4) ────────────────────────────────────
    [SkippableFact]
    public async Task UC_ATTUI_03_multiple_files_per_slot_all_returned()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await _fx.SeedPoliciesAsync(new PolicySpec(AsnEntity, TestCert, AttachmentRequirement.Mandatory));

        // A FRESH owner instance (the read-model keys purely on (ownerEntityType, ownerEntityId)) so prior tests'
        // uploads on the shared seeded AsnId never leak in — the suite shares ONE DB serially.
        var ownerId = Guid.NewGuid();

        // Three test certificates under the same (owner, type).
        var id1 = await _fx.AddUploadAsync(AsnEntity, ownerId, TestCert);
        var id2 = await _fx.AddUploadAsync(AsnEntity, ownerId, TestCert);
        var id3 = await _fx.AddUploadAsync(AsnEntity, ownerId, TestCert);

        var slots = await PanelAsync(AsnEntity, ownerId);

        var slot = slots.Should().ContainSingle(s => s.TypeCode == TestCert).Subject;
        slot.Documents.Should().HaveCount(3);
        slot.Documents.Select(d => d.Id).Should().BeEquivalentTo(new[] { id1, id2, id3 });
        // §13.8 — each file carries a files/proxy/{id} download url.
        slot.Documents.Should().OnlyContain(d => d.DownloadUrl == $"files/proxy/{d.Id}");
    }

    // ── §13.7 — entity-agnostic reuse: the SAME query renders on the Invoice entity ─────────────────────
    [SkippableFact]
    public async Task UC_ATTUI_06_same_query_on_invoice_entity()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // Policy rows exist for the Invoice entity only (NOT the ASN entity).
        await _fx.SeedPoliciesAsync(
            new PolicySpec(InvoiceEntity, TestCert, AttachmentRequirement.Mandatory),
            new PolicySpec(InvoiceEntity, PackingSlip, AttachmentRequirement.Warning));

        // A FRESH owner instance so prior tests' uploads on the shared seeded InvoiceId never leak in.
        var invoiceOwnerId = Guid.NewGuid();

        // One file under the Invoice TestCertificate slot.
        await _fx.AddUploadAsync(InvoiceEntity, invoiceOwnerId, TestCert);

        var slots = await PanelAsync(InvoiceEntity, invoiceOwnerId);

        slots.Should().HaveCount(2);
        // §13.3 — Mandatory (TestCertificate) before Warning (PackingSlip).
        slots[0].TypeCode.Should().Be(TestCert);
        slots[0].Requirement.Should().Be(nameof(AttachmentRequirement.Mandatory));
        slots[0].Documents.Should().ContainSingle();
        slots[1].TypeCode.Should().Be(PackingSlip);
        slots[1].Requirement.Should().Be(nameof(AttachmentRequirement.Warning));
        slots[1].Documents.Should().BeEmpty();

        // The ASN entity has NO Invoice-entity policy → its panel stays empty (proves the panel is entity-keyed).
        var asnSlots = await PanelAsync(AsnEntity, Guid.NewGuid());
        asnSlots.Should().BeEmpty();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────
    private async Task<List<AttachmentPanelSlotDto>> PanelAsync(string entity, Guid id)
    {
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var resp = await client.GetAsync($"/api/attachments/panel?entity={entity}&id={id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        var stream = await resp.Content.ReadAsStreamAsync();
        var result = (await JsonSerializer.DeserializeAsync<Result<List<AttachmentPanelSlotDto>>>(stream, Json))!;
        result.Success.Should().BeTrue();
        return result.Data!;
    }
}
