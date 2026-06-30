using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Domain.Enums;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R4 — TSD R4 Addendum §6.2 / Component 3 (PO Confirmation Gate). Pure, DB-less tests for the ship-gate matrix.
/// Parametrised over EVERY (PoStatus × PoConfirmationMode) cell, asserting Allow/Block exactly — including the D2
/// rule that BOTH Negotiation and Approved BLOCK in all three modes (they replace the removed DateProposed row).
///
/// <para>Matrix (§6.2):
/// <code>
/// PoStatus            | AutoAccept | AcknowledgeToShip | AcceptToShip
/// Draft               | Block      | Block             | Block
/// Released            | Allow      | Block             | Block
/// Acknowledged        | Allow      | Allow             | Block
/// Accepted            | Allow      | Allow             | Allow
/// Rejected            | Block      | Block             | Block
/// Negotiation         | Block      | Block             | Block   (replaces DateProposed)
/// Approved            | Block      | Block             | Block   (replaces DateProposed)
/// PartiallyDelivered  | Allow      | Allow             | Allow
/// FullyShipped        | Allow      | Allow             | Allow   (R5 §11.2 — balance=0 blocks at Submit)
/// Delivered           | Block      | Block             | Block   (balance-driven no-op)
/// Closed              | Block      | Block             | Block
/// Cancelled           | Block      | Block             | Block
/// </code>
/// </para>
/// </summary>
public class PoConfirmationPolicyTests
{
    // ── AutoAccept ──────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(PoStatus.Draft, false)]
    [InlineData(PoStatus.Released, true)]
    [InlineData(PoStatus.Acknowledged, true)]
    [InlineData(PoStatus.Accepted, true)]
    [InlineData(PoStatus.Rejected, false)]
    [InlineData(PoStatus.Negotiation, false)]   // D2 — replaces DateProposed; blocks.
    [InlineData(PoStatus.Approved, false)]      // D2 — replaces DateProposed; blocks.
    [InlineData(PoStatus.PartiallyDelivered, true)]
    [InlineData(PoStatus.FullyShipped, true)]   // R5 §11.2 — shippable at the gate; balance=0 blocks at Submit.
    [InlineData(PoStatus.Delivered, false)]
    [InlineData(PoStatus.Closed, false)]
    [InlineData(PoStatus.Cancelled, false)]
    public void AutoAccept(PoStatus status, bool expected)
        => PoConfirmationPolicy.AllowsShipping(status, PoConfirmationMode.AutoAccept).Should().Be(expected);

    // ── AcknowledgeToShip ───────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(PoStatus.Draft, false)]
    [InlineData(PoStatus.Released, false)]
    [InlineData(PoStatus.Acknowledged, true)]
    [InlineData(PoStatus.Accepted, true)]
    [InlineData(PoStatus.Rejected, false)]
    [InlineData(PoStatus.Negotiation, false)]
    [InlineData(PoStatus.Approved, false)]
    [InlineData(PoStatus.PartiallyDelivered, true)]
    [InlineData(PoStatus.FullyShipped, true)]   // R5 §11.2 — shippable at the gate; balance=0 blocks at Submit.
    [InlineData(PoStatus.Delivered, false)]
    [InlineData(PoStatus.Closed, false)]
    [InlineData(PoStatus.Cancelled, false)]
    public void AcknowledgeToShip(PoStatus status, bool expected)
        => PoConfirmationPolicy.AllowsShipping(status, PoConfirmationMode.AcknowledgeToShip).Should().Be(expected);

    // ── AcceptToShip (default) ──────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(PoStatus.Draft, false)]
    [InlineData(PoStatus.Released, false)]
    [InlineData(PoStatus.Acknowledged, false)]
    [InlineData(PoStatus.Accepted, true)]
    [InlineData(PoStatus.Rejected, false)]
    [InlineData(PoStatus.Negotiation, false)]
    [InlineData(PoStatus.Approved, false)]
    [InlineData(PoStatus.PartiallyDelivered, true)]
    [InlineData(PoStatus.FullyShipped, true)]   // R5 §11.2 — shippable at the gate; balance=0 blocks at Submit.
    [InlineData(PoStatus.Delivered, false)]
    [InlineData(PoStatus.Closed, false)]
    [InlineData(PoStatus.Cancelled, false)]
    public void AcceptToShip(PoStatus status, bool expected)
        => PoConfirmationPolicy.AllowsShipping(status, PoConfirmationMode.AcceptToShip).Should().Be(expected);

    // ── Belt-and-braces: Negotiation + Approved BLOCK in EVERY mode (D2 / §6.2 — replace DateProposed) ─
    [Theory]
    [InlineData(PoConfirmationMode.AutoAccept)]
    [InlineData(PoConfirmationMode.AcknowledgeToShip)]
    [InlineData(PoConfirmationMode.AcceptToShip)]
    public void Negotiation_blocks_in_every_mode(PoConfirmationMode mode)
        => PoConfirmationPolicy.AllowsShipping(PoStatus.Negotiation, mode).Should().BeFalse();

    [Theory]
    [InlineData(PoConfirmationMode.AutoAccept)]
    [InlineData(PoConfirmationMode.AcknowledgeToShip)]
    [InlineData(PoConfirmationMode.AcceptToShip)]
    public void Approved_blocks_in_every_mode(PoConfirmationMode mode)
        => PoConfirmationPolicy.AllowsShipping(PoStatus.Approved, mode).Should().BeFalse();

    // ── The required-action label drives the block message. ────────────────────────────────────────
    [Theory]
    [InlineData(PoConfirmationMode.AcceptToShip, "Accept")]
    [InlineData(PoConfirmationMode.AcknowledgeToShip, "Acknowledge")]
    [InlineData(PoConfirmationMode.AutoAccept, "Accept")]
    public void RequiredAction(PoConfirmationMode mode, string expected)
        => PoConfirmationPolicy.RequiredAction(mode).Should().Be(expected);
}
