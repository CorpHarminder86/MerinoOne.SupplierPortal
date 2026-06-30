using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Domain.Enums;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R5 — TSD R5 Addendum §11.3, Component 7 (ERP→Portal PO Status Mapping). Pure, DB-less tests for the
/// inbound-PO status derivation rule. The resolver COMPOSES the mapped candidate (from the configurable
/// PoStatusMapping) with the material-change flag (from PoMaterialChange) — these tests exercise the §11.3
/// branches directly: unmapped, first receipt, terminal, Released re-arm gating, Draft-on-re-receipt, Delivered.
///
/// <para>Covers the §17 use cases: UC-SM-01 (many→one), UC-SM-03 (material re-receipt re-arms), UC-SM-04
/// (non-material does NOT clobber), UC-SM-05 (terminal unconditional), UC-SM-06 (unmapped → no change +
/// would-fail-to-synclog), plus UC-SM-08 (FullyShipped → Delivered owned by ERP).</para>
/// </summary>
public class PoStatusResolverTests
{
    // ── UC-SM-06 — UNMAPPED ERP status → no change, flagged so the caller writes a Sync Log Failed row. ──
    [Fact]
    public void Unmapped_first_receipt_leaves_unchanged_and_flags()
    {
        var res = PoStatusResolver.Resolve(currentStatus: null, mappedCandidate: null, isMaterialChange: false);
        res.Unmapped.Should().BeTrue();
        res.NewStatus.Should().BeNull(because: "an unmapped status never derives a PoStatus (no silent guess)");
    }

    [Fact]
    public void Unmapped_re_receipt_leaves_existing_status_and_flags()
    {
        // PO is Accepted; the ERP sends a status with no mapping row → nothing changes, but it must be flagged.
        var res = PoStatusResolver.Resolve(currentStatus: PoStatus.Accepted, mappedCandidate: null, isMaterialChange: true);
        res.Unmapped.Should().BeTrue();
        res.NewStatus.Should().BeNull(because: "unmapped does not clobber the supplier's Accepted progress");
    }

    // ── UC-SM-01 — many ERP statuses map to one portal status; on a FIRST receipt the candidate is taken. ──
    [Theory]
    [InlineData(PoStatus.Released)]   // e.g. Approved / Released / Sent / modified all map here
    [InlineData(PoStatus.Draft)]      // e.g. Draft / Created
    public void First_receipt_takes_the_mapped_candidate(PoStatus candidate)
    {
        var res = PoStatusResolver.Resolve(currentStatus: null, mappedCandidate: candidate, isMaterialChange: false);
        res.Unmapped.Should().BeFalse();
        res.NewStatus.Should().Be(candidate, because: "the first receipt of a PO takes the mapped status");
    }

    // ── UC-SM-03 — MATERIAL re-receipt mapped to Released RE-ARMS confirmation (resets to Released). ──
    [Fact]
    public void Material_re_receipt_to_Released_re_arms_confirmation()
    {
        // PO is Accepted (supplier-set); ERP "modified" (→ Released) with a MATERIAL change.
        var res = PoStatusResolver.Resolve(currentStatus: PoStatus.Accepted, mappedCandidate: PoStatus.Released, isMaterialChange: true);
        res.Unmapped.Should().BeFalse();
        res.NewStatus.Should().Be(PoStatus.Released, because: "a material change re-arms the confirmation gate (R4 §6.3)");
    }

    // ── UC-SM-04 — NON-material re-receipt mapped to Released does NOT clobber supplier progress. ──
    [Fact]
    public void NonMaterial_re_receipt_to_Released_does_not_clobber()
    {
        var res = PoStatusResolver.Resolve(currentStatus: PoStatus.Accepted, mappedCandidate: PoStatus.Released, isMaterialChange: false);
        res.Unmapped.Should().BeFalse();
        res.NewStatus.Should().BeNull(because: "a routine non-material re-sync must NOT reset an Accepted PO to Released");
    }

    // ── UC-SM-05 — TERMINAL ERP states (Cancelled / Closed) apply UNCONDITIONALLY on a re-receipt. ──
    [Theory]
    [InlineData(PoStatus.Cancelled, true)]
    [InlineData(PoStatus.Cancelled, false)]
    [InlineData(PoStatus.Closed, true)]
    [InlineData(PoStatus.Closed, false)]
    public void Terminal_re_receipt_applies_unconditionally(PoStatus terminal, bool isMaterial)
    {
        var res = PoStatusResolver.Resolve(currentStatus: PoStatus.Accepted, mappedCandidate: terminal, isMaterialChange: isMaterial);
        res.Unmapped.Should().BeFalse();
        res.NewStatus.Should().Be(terminal, because: "terminal ERP states win regardless of material-change evaluation");
    }

    // ── Draft on a RE-receipt is a first-receipt-only state → ignored (no clobber). ──
    [Fact]
    public void Draft_re_receipt_is_ignored()
    {
        var res = PoStatusResolver.Resolve(currentStatus: PoStatus.Accepted, mappedCandidate: PoStatus.Draft, isMaterialChange: true);
        res.Unmapped.Should().BeFalse();
        res.NewStatus.Should().BeNull(because: "Draft is a first-receipt state — ignore it on a re-receipt");
    }

    // ── UC-SM-08 — Delivered (ERP-driven) advances a FullyShipped PO; full shipment alone never sets it. ──
    [Fact]
    public void Delivered_re_receipt_advances_a_fully_shipped_po()
    {
        var res = PoStatusResolver.Resolve(currentStatus: PoStatus.FullyShipped, mappedCandidate: PoStatus.Delivered, isMaterialChange: false);
        res.Unmapped.Should().BeFalse();
        res.NewStatus.Should().Be(PoStatus.Delivered, because: "the mapped ERP Delivered status advances FullyShipped → Delivered (UC-SM-08)");
    }
}
