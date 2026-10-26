using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Controllers;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R17 — <c>POST /api/integration/inbound/asn-update</c> (X-APIKey, <c>Integration.Inbound.AsnUpdate</c>), exercised through the REAL
/// host against the dedicated SQL test DB. LN reports the shipment status and per-line received quantity / actual delivery date of an
/// ASN the portal already POSTED. Pinned here: the F2 sample shape is accepted verbatim (upper-case <c>ASN</c> array, <c>supplierASN</c>
/// key); the match is (erpSupplierCode → supplier, supplierASN → ASN number, positionNo [+ poNumber] → ASN line); the shipment stamps a
/// blank ERP code and otherwise must agree; only a posted ASN may be updated; the status is stored verbatim and only
/// InTransit/Delivered/Received also move the portal state; a business failure is a Failed ROW (200), never a 4xx; and the write is
/// atomic per ASN.
///
/// <para>The test DB is persistent: every test seeds its OWN uniquely tagged supplier/PO/ASN (the ASN number is globally unique) directly
/// through <see cref="AppDbContext"/>, and never touches the fixture's shared rows, so re-runs and sibling tests are unaffected.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnUpdateInboundTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Url = "/api/integration/inbound/asn-update";
    private static readonly DateTime Jul15 = new(2026, 7, 15);

    private readonly IntegrationTestFixture _fx;
    public AsnUpdateInboundTests(IntegrationTestFixture fx) => _fx = fx;

    // ── the F2 sample, on a Submitted ASN: both lines, the status mapping and the shipment stamp ───────
    [SkippableFact]
    public async Task Sample_shaped_payload_on_a_submitted_asn_writes_both_lines_and_moves_the_asn_to_received()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();
        var (resp, result) = await PostAsync(Payload(s, "INB00169", "Received",
            Line(10, 20m, "2026-07-15"), Line(20, 10m, "2026-07-15")));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        (result.Received, result.Updated, result.Skipped, result.Failed).Should().Be((1, 1, 0, 0), because: Describe(result));
        result.Rows.Should().ContainSingle().Which.Should().Match<RowResult>(r => r.Code == s.AsnNumber && r.Outcome == RowOutcome.Updated);

        var asn = await LoadAsync(s.AsnId);
        asn.AsnStatus.Should().Be(AsnStatus.Received);
        asn.ErpShipmentStatus.Should().Be("Received");
        asn.ErpCode.Should().Be("INB00169", "the blank ERP code is stamped from the shipment");
        var l10 = asn.Lines.Single(l => l.PositionNo == 10);
        var l20 = asn.Lines.Single(l => l.PositionNo == 20);
        l10.ReceivedQty.Should().Be(20m);
        l10.ActualDeliveryDate.Should().Be(Jul15);
        l20.ReceivedQty.Should().Be(10m);
        l20.ActualDeliveryDate.Should().Be(Jul15);
        asn.UpdatedOn.Should().NotBeNull("the ASN header was changed, so its audit block is stamped");
    }

    [SkippableFact]
    public async Task A_blank_erp_code_is_stamped_from_the_shipment_and_a_matching_shipment_leaves_it_alone()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var blank = await SeedAsync();
        var held = await SeedAsync(erpCode: "INB-HELD-1");

        var (_, r1) = await PostAsync(Payload(blank, "  INB-NEW-1  ", "InTransit"));
        r1.Updated.Should().Be(1, because: Describe(r1));
        (await LoadAsync(blank.AsnId)).ErpCode.Should().Be("INB-NEW-1", "the shipment is trimmed before it is stamped");

        // Same shipment, different case → accepted (the comparison is case-insensitive) and the stored code is NOT rewritten.
        var (_, r2) = await PostAsync(Payload(held, "inb-held-1", "InTransit"));
        r2.Updated.Should().Be(1, because: Describe(r2));
        (await LoadAsync(held.AsnId)).ErpCode.Should().Be("INB-HELD-1");
    }

    // ── a shipment that disagrees with the one the ASN already carries fails the record and writes nothing ──
    [SkippableFact]
    public async Task A_shipment_mismatch_fails_the_asn_and_writes_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync(erpCode: "INB-ORIG");
        var (resp, result) = await PostAsync(Payload(s, "INB-OTHER", "Received", Line(10, 20m, "2026-07-15")));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: "a business failure is a Failed row, not a 4xx");
        result.Failed.Should().Be(1);
        var row = result.Rows.Should().ContainSingle().Which;
        row.Code.Should().Be(s.AsnNumber);
        row.Outcome.Should().Be(RowOutcome.Failed);
        row.Error.Should().Be($"Shipment mismatch: ASN '{s.AsnNumber}' carries 'INB-ORIG', ERP sent 'INB-OTHER'.");

        var asn = await LoadAsync(s.AsnId);
        asn.ErpCode.Should().Be("INB-ORIG");
        asn.AsnStatus.Should().Be(AsnStatus.Submitted);
        asn.ErpShipmentStatus.Should().BeNull();
        asn.Lines.Should().OnlyContain(l => l.ReceivedQty == null && l.ActualDeliveryDate == null);
    }

    // ── only a posted ASN can be updated by ERP ─────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task A_draft_asn_fails_because_only_posted_asns_can_be_updated()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var draft = await SeedAsync(AsnStatus.Draft);
        var approved = await SeedAsync(AsnStatus.Approved);     // buyer-confirmed but NOT yet posted
        var (_, result) = await PostAsync(Batch(
            Rec(draft, "INB-D", "Received", Line(10, 1m, "2026-07-15")),
            Rec(approved, "INB-A", "Received", Line(10, 1m, "2026-07-15"))));

        result.Failed.Should().Be(2, because: Describe(result));
        result.Rows.Single(r => r.Code == draft.AsnNumber).Error
            .Should().Be($"ASN '{draft.AsnNumber}' is 'Draft'; only posted ASNs can be updated by ERP.");
        result.Rows.Single(r => r.Code == approved.AsnNumber).Error
            .Should().Be($"ASN '{approved.AsnNumber}' is 'Approved'; only posted ASNs can be updated by ERP.");

        foreach (var (s, expected) in new[] { (draft, AsnStatus.Draft), (approved, AsnStatus.Approved) })
        {
            var asn = await LoadAsync(s.AsnId);
            asn.AsnStatus.Should().Be(expected);
            asn.ErpShipmentStatus.Should().BeNull();
            asn.ErpCode.Should().BeNull();
            asn.Lines.Should().OnlyContain(l => l.ReceivedQty == null);
        }
    }

    // ── supplier / ASN resolution failures ──────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task An_unknown_erp_supplier_code_fails_and_a_real_supplier_that_does_not_own_the_asn_fails()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();
        var other = await SeedAsync();

        var ghost = $"NOSUCH-{Guid.NewGuid():N}"[..20];
        var (_, unknown) = await PostAsync(PayloadRaw(ghost, s.AsnNumber, "INB-U1", "Received"));
        unknown.Rows.Should().ContainSingle().Which.Should().Match<RowResult>(r =>
            r.Outcome == RowOutcome.Failed && r.Code == s.AsnNumber && r.Error == $"Unknown erpSupplierCode '{ghost}'.");

        // A supplier that exists but is NOT the ASN's supplier (the ASN number belongs to supplier `s`).
        var (_, foreign) = await PostAsync(PayloadRaw(other.ErpSupplierCode, s.AsnNumber, "INB-U2", "Received"));
        foreign.Rows.Should().ContainSingle().Which.Should().Match<RowResult>(r =>
            r.Outcome == RowOutcome.Failed
            && r.Error == $"ASN '{s.AsnNumber}' not found for supplier '{other.ErpSupplierCode}' in company '{IntegrationTestFixture.CompanyCode}'.");

        // A well-formed but non-existent ASN number.
        var missingAsn = $"ASN-MISSING-{s.Tag}";
        var (_, absent) = await PostAsync(PayloadRaw(s.ErpSupplierCode, missingAsn, "INB-U3", "Received"));
        absent.Rows.Should().ContainSingle().Which.Error.Should()
            .Be($"ASN '{missingAsn}' not found for supplier '{s.ErpSupplierCode}' in company '{IntegrationTestFixture.CompanyCode}'.");

        (await LoadAsync(s.AsnId)).Should().Match<Asn>(a => a.AsnStatus == AsnStatus.Submitted && a.ErpCode == null && a.ErpShipmentStatus == null);
    }

    // ── a position repeated across the POs of one ASN needs the poNumber ────────────────────────────────
    [SkippableFact]
    public async Task A_position_shared_by_two_pos_fails_without_ponumber_and_succeeds_with_it()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync(secondPo: true);     // lines: PO-A/10, PO-A/20, PO-B/10

        var (_, ambiguous) = await PostAsync(Payload(s, "INB-AMB", "Received", Line(10, 7m, "2026-07-15")));
        ambiguous.Rows.Should().ContainSingle().Which.Should().Match<RowResult>(r =>
            r.Outcome == RowOutcome.Failed
            && r.Error == "Position 10 matches 2 ASN lines; send poNumber to disambiguate.");
        (await LoadAsync(s.AsnId)).Lines.Should().OnlyContain(l => l.ReceivedQty == null, "an ambiguous record writes nothing");

        // Position 20 is unique, so it needs no poNumber; position 10 is pinned to PO-B (matched case-insensitively).
        var (_, pinned) = await PostAsync(Payload(s, "INB-AMB", "Received",
            Line(10, 7m, "2026-07-15", s.PoNumberB!.ToLowerInvariant()), Line(20, 3m, "2026-07-16")));
        pinned.Updated.Should().Be(1, because: Describe(pinned));

        var asn = await LoadAsync(s.AsnId);
        asn.Lines.Single(l => l.PurchaseOrderLineId == s.PoBLine10).ReceivedQty.Should().Be(7m);
        asn.Lines.Single(l => l.PurchaseOrderLineId == s.PoALine10).ReceivedQty.Should().BeNull("PO-A's position 10 was not addressed");
        asn.Lines.Single(l => l.PositionNo == 20).ReceivedQty.Should().Be(3m);

        // A poNumber that no ASN line carries is a clear failure naming it.
        var (_, wrongPo) = await PostAsync(Payload(s, "INB-AMB", "Received", Line(20, 3m, "2026-07-16", "PO-NOT-ON-THIS-ASN")));
        wrongPo.Rows.Should().ContainSingle().Which.Error.Should().Be("No ASN line at position 20 on PO PO-NOT-ON-THIS-ASN.");
    }

    // ── a status LN uses that the portal has no state for is kept verbatim and moves nothing ─────────────
    [SkippableFact]
    public async Task An_unmapped_status_is_stored_verbatim_and_leaves_the_asn_status_unchanged()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();
        var (_, result) = await PostAsync(Payload(s, "INB-CLOSED", "Closed", Line(10, 20m, "2026-07-15")));

        result.Updated.Should().Be(1, because: Describe(result));
        var asn = await LoadAsync(s.AsnId);
        asn.ErpShipmentStatus.Should().Be("Closed");
        asn.AsnStatus.Should().Be(AsnStatus.Submitted, "Closed is not InTransit/Delivered/Received");
        asn.Lines.Single(l => l.PositionNo == 10).ReceivedQty.Should().Be(20m, "the lines are still written");
    }

    // ── the status mapping tolerates spaces/hyphens/underscores and may move backwards ──────────────────
    [SkippableFact]
    public async Task Status_mapping_ignores_spaces_hyphens_and_case_and_may_move_the_asn_backwards()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync(AsnStatus.Received, erpCode: "INB-BACK");

        var (_, back) = await PostAsync(Payload(s, "INB-BACK", "In Transit"));
        back.Updated.Should().Be(1, because: Describe(back));
        var asn = await LoadAsync(s.AsnId);
        (asn.AsnStatus, asn.ErpShipmentStatus).Should().Be((AsnStatus.InTransit, "In Transit"));

        foreach (var (text, expected) in new[] { ("DELIVERED", AsnStatus.Delivered), ("in-transit", AsnStatus.InTransit), ("RECEIVED", AsnStatus.Received) })
        {
            var (_, r) = await PostAsync(Payload(s, "INB-BACK", text));
            r.Updated.Should().Be(1, because: $"'{text}' — {Describe(r)}");
            var now = await LoadAsync(s.AsnId);
            (now.AsnStatus, now.ErpShipmentStatus).Should().Be((expected, text));
        }
    }

    // ── a header-only update (no lines) ─────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task A_header_only_update_without_lines_is_updated()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();

        // Once with `lines` absent altogether, once (below) with an empty array — both are header-only.
        var json = JsonSerializer.Serialize(new
        {
            companyCode = IntegrationTestFixture.CompanyCode,
            ASN = new[] { new { erpSupplierCode = s.ErpSupplierCode, supplierASN = s.AsnNumber, shipment = "INB-HDR", status = "Delivered" } },
        });
        var (resp, result) = await PostAsync(json);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        result.Updated.Should().Be(1, because: Describe(result));
        var asn = await LoadAsync(s.AsnId);
        asn.AsnStatus.Should().Be(AsnStatus.Delivered);
        asn.ErpShipmentStatus.Should().Be("Delivered");
        asn.ErpCode.Should().Be("INB-HDR");
        asn.Lines.Should().OnlyContain(l => l.ReceivedQty == null && l.ActualDeliveryDate == null);

        var (_, empty) = await PostAsync(Payload(s, "INB-HDR", "Received"));     // empty `lines` array
        empty.Updated.Should().Be(1, because: Describe(empty));
        (await LoadAsync(s.AsnId)).AsnStatus.Should().Be(AsnStatus.Received);
    }

    // ── an update that changes nothing is Skipped; an omitted quantity/date leaves the stored one alone ──
    [SkippableFact]
    public async Task An_unchanged_update_is_skipped_and_omitted_values_leave_the_stored_ones_alone()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();
        (await PostAsync(Payload(s, "INB-SK", "Received", Line(10, 20m, "2026-07-15")))).Result.Updated.Should().Be(1);

        // Same facts under a fresh key (not a hash replay) → nothing differs → Skipped.
        var (_, same) = await PostAsync(Payload(s, "INB-SK", "Received", Line(10, 20m, "2026-07-15")), key: $"asnu-{Guid.NewGuid():N}");
        (same.Updated, same.Skipped).Should().Be((0, 1), because: Describe(same));

        // Line 10 sent with no quantity/date at all, line 20 with a quantity: line 10 keeps 20 / Jul 15; line 20 is set.
        var json = JsonSerializer.Serialize(new
        {
            companyCode = IntegrationTestFixture.CompanyCode,
            ASN = new[]
            {
                new
                {
                    erpSupplierCode = s.ErpSupplierCode, supplierASN = s.AsnNumber, shipment = "INB-SK", status = "Received",
                    lines = new object[] { new { positionNo = 10 }, new { positionNo = 20, receivedQty = 4.5 } },
                },
            },
        });
        var (_, partial) = await PostAsync(json);
        partial.Updated.Should().Be(1, because: Describe(partial));
        var asn = await LoadAsync(s.AsnId);
        var l10 = asn.Lines.Single(l => l.PositionNo == 10);
        var l20 = asn.Lines.Single(l => l.PositionNo == 20);
        l10.ReceivedQty.Should().Be(20m, "a null/omitted value leaves the stored value unchanged");
        l10.ActualDeliveryDate.Should().Be(Jul15, "a null/omitted value leaves the stored value unchanged");
        l20.ReceivedQty.Should().Be(4.5m);
        l20.ActualDeliveryDate.Should().BeNull();

        // receivedQty is an ABSOLUTE replacement, not a delta.
        var (_, replace) = await PostAsync(Payload(s, "INB-SK", "Received", Line(10, 5m, null)));
        replace.Updated.Should().Be(1, because: Describe(replace));
        (await LoadAsync(s.AsnId)).Lines.Single(l => l.PositionNo == 10).ReceivedQty.Should().Be(5m);
    }

    // ── atomic per ASN: one bad line fails the record and writes nothing for it; the rest of the batch proceeds ──
    [SkippableFact]
    public async Task One_unmatched_line_fails_the_whole_asn_record_but_not_the_other_asns_in_the_batch()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var bad = await SeedAsync();
        var good = await SeedAsync();
        var (resp, result) = await PostAsync(Batch(
            Rec(bad, "INB-BAD", "Received", Line(10, 20m, "2026-07-15"), Line(99, 1m, "2026-07-15")),   // 99 matches nothing
            Rec(good, "INB-GOOD", "Received", Line(10, 8m, "2026-07-15"))));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        (result.Received, result.Updated, result.Failed).Should().Be((2, 1, 1), because: Describe(result));
        result.Rows.Single(r => r.Code == bad.AsnNumber).Error.Should().Be("No ASN line at position 99.");
        result.Rows.Single(r => r.Code == good.AsnNumber).Outcome.Should().Be(RowOutcome.Updated);

        var badAsn = await LoadAsync(bad.AsnId);
        badAsn.AsnStatus.Should().Be(AsnStatus.Submitted, "nothing at all is written for a failed ASN record");
        badAsn.ErpShipmentStatus.Should().BeNull();
        badAsn.ErpCode.Should().BeNull();
        badAsn.Lines.Should().OnlyContain(l => l.ReceivedQty == null && l.ActualDeliveryDate == null, "not even the good line 10 was written");

        var goodAsn = await LoadAsync(good.AsnId);
        goodAsn.AsnStatus.Should().Be(AsnStatus.Received);
        goodAsn.Lines.Single(l => l.PositionNo == 10).ReceivedQty.Should().Be(8m);
    }

    // ── two payload lines that land on the same ASN line fail the record ────────────────────────────────
    [SkippableFact]
    public async Task Two_payload_lines_resolving_to_the_same_asn_line_fail_the_record()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();
        // (10, no PO) and (10, PO-A) are distinct line keys, but both resolve to the single ASN line at position 10.
        var (_, result) = await PostAsync(Payload(s, "INB-DUP", "Received",
            Line(10, 1m, "2026-07-15"), Line(10, 2m, "2026-07-15", s.PoNumberA)));

        result.Rows.Should().ContainSingle().Which.Should().Match<RowResult>(r =>
            r.Outcome == RowOutcome.Failed && r.Error!.Contains("position 10", StringComparison.Ordinal));
        (await LoadAsync(s.AsnId)).Lines.Should().OnlyContain(l => l.ReceivedQty == null);
    }

    // ── request validation → 400 (and nothing is written) ───────────────────────────────────────────────
    [SkippableFact]
    public async Task Invalid_requests_are_rejected_with_400_and_write_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();

        async Task AssertBadRequest(string because, string json)
        {
            var (resp, _) = await PostAsync(json);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: because + " — " + await Body(resp));
        }

        string Body1(object line) => JsonSerializer.Serialize(new
        {
            companyCode = IntegrationTestFixture.CompanyCode,
            ASN = new[] { new { erpSupplierCode = s.ErpSupplierCode, supplierASN = s.AsnNumber, shipment = "INB-V", status = "Received", lines = new[] { line } } },
        });

        await AssertBadRequest("a negative receivedQty is invalid", Body1(new { positionNo = 10, receivedQty = -1 }));
        await AssertBadRequest("positionNo 0 is invalid", Body1(new { positionNo = 0, receivedQty = 1 }));
        await AssertBadRequest("a negative positionNo is invalid", Body1(new { positionNo = -10, receivedQty = 1 }));

        await AssertBadRequest("a duplicate (positionNo, poNumber) line key is invalid", JsonSerializer.Serialize(new
        {
            companyCode = IntegrationTestFixture.CompanyCode,
            ASN = new[]
            {
                new
                {
                    erpSupplierCode = s.ErpSupplierCode, supplierASN = s.AsnNumber, shipment = "INB-V", status = "Received",
                    lines = new[]
                    {
                        new { positionNo = 10, receivedQty = 1, poNumber = (string?)"PO-X" },
                        new { positionNo = 10, receivedQty = 2, poNumber = (string?)"po-x" },
                    },
                },
            },
        }));
        await AssertBadRequest("two lines without a poNumber at the same position are a duplicate key (null is its own key)",
            JsonSerializer.Serialize(new
            {
                companyCode = IntegrationTestFixture.CompanyCode,
                ASN = new[]
                {
                    new
                    {
                        erpSupplierCode = s.ErpSupplierCode, supplierASN = s.AsnNumber, shipment = "INB-V", status = "Received",
                        lines = new[] { new { positionNo = 10, receivedQty = 1 }, new { positionNo = 10, receivedQty = 2 } },
                    },
                },
            }));

        await AssertBadRequest("a blank companyCode is invalid", JsonSerializer.Serialize(new
        {
            companyCode = "", ASN = new[] { new { erpSupplierCode = s.ErpSupplierCode, supplierASN = s.AsnNumber, shipment = "INB-V", status = "Received" } },
        }));
        await AssertBadRequest("an empty ASN array is invalid",
            JsonSerializer.Serialize(new { companyCode = IntegrationTestFixture.CompanyCode, ASN = Array.Empty<object>() }));
        await AssertBadRequest("a status over 30 characters is invalid", JsonSerializer.Serialize(new
        {
            companyCode = IntegrationTestFixture.CompanyCode,
            ASN = new[] { new { erpSupplierCode = s.ErpSupplierCode, supplierASN = s.AsnNumber, shipment = "INB-V", status = new string('s', 31) } },
        }));
        await AssertBadRequest("a shipment longer than the ERP-code column is invalid", JsonSerializer.Serialize(new
        {
            companyCode = IntegrationTestFixture.CompanyCode,
            ASN = new[] { new { erpSupplierCode = s.ErpSupplierCode, supplierASN = s.AsnNumber, shipment = new string('h', 51), status = "Received" } },
        }));
        foreach (var blankField in new[] { "erpSupplierCode", "supplierASN", "shipment", "status" })
        {
            var rec = new Dictionary<string, object?>
            {
                ["erpSupplierCode"] = s.ErpSupplierCode, ["supplierASN"] = s.AsnNumber, ["shipment"] = "INB-V", ["status"] = "Received",
            };
            rec[blankField] = " ";
            await AssertBadRequest($"a blank {blankField} is invalid",
                JsonSerializer.Serialize(new { companyCode = IntegrationTestFixture.CompanyCode, ASN = new[] { rec } }));
        }

        var asn = await LoadAsync(s.AsnId);
        asn.AsnStatus.Should().Be(AsnStatus.Submitted);
        asn.ErpShipmentStatus.Should().BeNull();
        asn.ErpCode.Should().BeNull();
        asn.Lines.Should().OnlyContain(l => l.ReceivedQty == null);
    }

    // ── scope gate ──────────────────────────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Push_with_a_key_lacking_the_asn_update_scope_is_403_and_writes_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();
        var client = await _fx.CreateScopedInboundClientAsync("Integration.Inbound.Grn", "Integration.Inbound.GrnReceipt");

        var resp = await client.PostAsync(Url,
            new StringContent(Payload(s, "INB-403", "Received", Line(10, 20m, "2026-07-15")), Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "the key lacks Integration.Inbound.AsnUpdate, so the [Authorize] policy denies the request");
        var asn = await LoadAsync(s.AsnId);
        asn.AsnStatus.Should().Be(AsnStatus.Submitted);
        asn.ErpShipmentStatus.Should().BeNull();
    }

    // ── idempotency: replaying a delivery with the same Idempotency-Key is a no-op ──────────────────────
    [SkippableFact]
    public async Task Replaying_the_same_idempotency_key_is_skipped_and_writes_nothing_more()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var s = await SeedAsync();
        var key = $"asnu-replay-{Guid.NewGuid():N}";
        var body = Payload(s, "INB-IDEM", "Received", Line(10, 20m, "2026-07-15"));

        var (_, first) = await PostAsync(body, key: key);
        first.Updated.Should().Be(1, because: Describe(first));

        // Between deliveries ERP's view moves on (the ASN is walked back), so a genuine re-run would visibly change the ASN again.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Asns.IgnoreQueryFilters().Where(a => a.Id == s.AsnId)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.AsnStatus, AsnStatus.InTransit));
        }

        var (resp, replay) = await PostAsync(body, key: key);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: "a replay is a 200 no-op, not an error");
        (replay.Updated, replay.Skipped).Should().Be((0, 1), because: Describe(replay));
        (await LoadAsync(s.AsnId)).AsnStatus.Should().Be(AsnStatus.InTransit, "the replay did not re-run the update");
    }

    // ── docs: the developer-docs catalog sample IS the F2 sample and is a valid request body ───────────
    [Fact]
    public void The_catalog_sample_for_asn_update_is_the_f2_sample_and_deserializes_into_the_request_contract()
    {
        var entry = IntegrationCatalog.All.Single(e => e.Scope == "Integration.Inbound.AsnUpdate");
        entry.Path.Should().Be("api/integration/inbound/asn-update");

        // The key names are exactly the ones LN sends: an upper-case `ASN` array and `supplierASN`.
        using (var doc = JsonDocument.Parse(entry.SampleJson))
        {
            doc.RootElement.TryGetProperty("ASN", out var asnArray).Should().BeTrue("the array key is upper-case ASN");
            asnArray[0].TryGetProperty("supplierASN", out _).Should().BeTrue("the ASN reference key is supplierASN");
        }

        var body = JsonSerializer.Deserialize<PushAsnUpdatesRequest>(entry.SampleJson, Json)!;
        body.CompanyCode.Should().Be("2000");
        var rec = body.Asn.Should().ContainSingle().Which;
        rec.ErpSupplierCode.Should().Be("BUS000048");
        rec.SupplierAsn.Should().Be("ASN-S0013-20261006115014829");
        rec.Shipment.Should().Be("INB00169");
        rec.Status.Should().Be("Received");
        rec.Lines.Should().Equal(
            new AsnUpdateLineRecord(10, 20m, Jul15),
            new AsnUpdateLineRecord(20, 10m, Jul15));
    }

    // ============================== seeding ==============================

    private sealed record Seeded(
        string Tag, string ErpSupplierCode, Guid SupplierId, Guid AsnId, string AsnNumber,
        string PoNumberA, Guid PoALine10, Guid PoALine20, string? PoNumberB, Guid PoBLine10);

    /// <summary>
    /// Seeds one tagged supplier (with an ERP supplier code), a PO with lines 10 and 20, and an ASN in <paramref name="status"/> with one
    /// ASN line per PO line (snapshotting PositionNo). <paramref name="secondPo"/> adds a second PO (line 10) and an ASN line on it, so
    /// position 10 occurs on two POs of the same ASN. The ASN number is globally unique (UQ_Asn_asnNumber), so it carries the tag.
    /// </summary>
    private async Task<Seeded> SeedAsync(AsnStatus status = AsnStatus.Submitted, string? erpCode = null, bool secondPo = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var supplier = await _fx.CreateSupplierAsync(tag, IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        var erpSupplier = $"BUS-{tag}";

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        await db.Suppliers.IgnoreQueryFilters().Where(x => x.Id == supplier.SupplierId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ErpCode, erpSupplier));

        PurchaseOrder NewPo(string number) => new()
        {
            Id = Guid.NewGuid(), PoNumber = number, SupplierId = supplier.SupplierId, PoType = PoType.Material,
            PoDate = now.Date, PoStatus = PoStatus.Accepted, SeccodeId = supplier.SeccodeId,
            TenantId = IntegrationTestFixture.TenantId, TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed", CreatedOn = now,
        };
        PurchaseOrderLine NewPoLine(Guid poId, int position) => new()
        {
            Id = Guid.NewGuid(), PurchaseOrderId = poId, PositionNo = position, SequenceNo = 1, ItemCode = $"ITEM-{position}",
            OrderUnit = "EA", OrderQty = 100, PriceUnit = 1, Price = 10, CreatedBy = "seed", CreatedOn = now,
        };

        var poA = NewPo($"PO-AU-A-{tag}");
        var a10 = NewPoLine(poA.Id, 10);
        var a20 = NewPoLine(poA.Id, 20);
        db.PurchaseOrders.Add(poA);
        db.PurchaseOrderLines.AddRange(a10, a20);

        PurchaseOrder? poB = null;
        PurchaseOrderLine? b10 = null;
        if (secondPo)
        {
            poB = NewPo($"PO-AU-B-{tag}");
            b10 = NewPoLine(poB.Id, 10);
            db.PurchaseOrders.Add(poB);
            db.PurchaseOrderLines.Add(b10);
        }
        await db.SaveChangesAsync();

        var asn = new Asn
        {
            Id = Guid.NewGuid(), AsnNumber = $"ASN-AU-{tag}", PurchaseOrderId = poA.Id, SupplierId = supplier.SupplierId,
            ExpectedDeliveryDate = now.Date.AddDays(1), AsnStatus = status, ErpCode = erpCode,
            SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId, TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed", CreatedOn = now,
        };
        db.Asns.Add(asn);
        AsnLine NewAsnLine(PurchaseOrderLine pl, decimal shipped) => new()
        {
            Id = Guid.NewGuid(), AsnId = asn.Id, PurchaseOrderLineId = pl.Id, ShippedQty = shipped,
            PositionNo = pl.PositionNo, SequenceNo = pl.SequenceNo, CreatedBy = "seed", CreatedOn = now,
        };
        db.AsnLines.Add(NewAsnLine(a10, 20));
        db.AsnLines.Add(NewAsnLine(a20, 10));
        if (b10 is not null) db.AsnLines.Add(NewAsnLine(b10, 5));
        await db.SaveChangesAsync();

        return new Seeded(tag, erpSupplier, supplier.SupplierId, asn.Id, asn.AsnNumber,
            poA.PoNumber, a10.Id, a20.Id, poB?.PoNumber, b10?.Id ?? Guid.Empty);
    }

    private async Task<Asn> LoadAsync(Guid asnId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Asns.IgnoreQueryFilters().AsNoTracking().Include(a => a.Lines)
            .FirstAsync(a => a.Id == asnId);
    }

    // ============================== payload builders (key names exactly as LN sends them) ==============================

    private static object Line(int positionNo, decimal? receivedQty, string? actualDeliveryDate, string? poNumber = null)
    {
        var line = new Dictionary<string, object?>
        {
            ["positionNo"] = positionNo, ["receivedQty"] = receivedQty, ["actualDeliveryDate"] = actualDeliveryDate,
        };
        if (poNumber is not null) line["poNumber"] = poNumber;
        return line;
    }

    /// <summary>One ASN record for the seeded ASN.</summary>
    private static object Rec(Seeded s, string shipment, string status, params object[] lines)
        => RecRaw(s.ErpSupplierCode, s.AsnNumber, shipment, status, lines);

    private static object RecRaw(string erpSupplierCode, string supplierAsn, string shipment, string status, params object[] lines)
        => new Dictionary<string, object?>
        {
            ["erpSupplierCode"] = erpSupplierCode, ["supplierASN"] = supplierAsn, ["shipment"] = shipment, ["status"] = status, ["lines"] = lines,
        };

    /// <summary>The whole request body for the given ASN records (the F2 sample shape: <c>companyCode</c> + an upper-case <c>ASN</c> array).</summary>
    private static string Batch(params object[] records)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["companyCode"] = IntegrationTestFixture.CompanyCode,
            ["ASN"] = records,
        });

    /// <summary>A body holding exactly one ASN record for the seeded ASN.</summary>
    private static string Payload(Seeded s, string shipment, string status, params object[] lines)
        => Batch(Rec(s, shipment, status, lines));

    private static string PayloadRaw(string erpSupplierCode, string supplierAsn, string shipment, string status, params object[] lines)
        => Batch(RecRaw(erpSupplierCode, supplierAsn, shipment, status, lines));

    /// <summary>Posts a body with the shared fixture key (carries the AsnUpdate scope); reads the result envelope only on a 2xx.</summary>
    private Task<(HttpResponseMessage Resp, UpsertResultDto Result)> PostAsync(string json, string? key = null)
        => PostAsync(_fx.CreateInboundClient(), json, key);

    private static async Task<(HttpResponseMessage Resp, UpsertResultDto Result)> PostAsync(HttpClient client, string json, string? key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        var resp = await client.SendAsync(req);
        UpsertResultDto? data = null;
        if (resp.IsSuccessStatusCode)
        {
            var stream = await resp.Content.ReadAsStreamAsync();
            data = (await JsonSerializer.DeserializeAsync<Result<UpsertResultDto>>(stream, Json))!.Data;
        }
        return (resp, data!);
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();

    private static string Describe(UpsertResultDto r)
        => string.Join("; ", r.Rows.Select(x => $"{x.Code}={x.Outcome}{(x.Error is null ? "" : $" ({x.Error})")}"));
}
