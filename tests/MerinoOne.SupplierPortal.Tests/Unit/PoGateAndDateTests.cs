using System.Globalization;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R12 — the seeded eligibility gates (D16) and the wire date format (D4), both DB-free.
///
/// <para>These two are grouped because they share a failure mode: each is a small string whose wrongness is
/// invisible until production. A gate that never matches silently stops every push; a date that formats
/// without a zone is silently reinterpreted by LN as UTC−05:00.</para>
/// </summary>
public class PoGateAndDateTests
{
    private readonly LnMappingService _svc = new();

    private bool Eligible(string transactionType, string inputJson)
    {
        var expr = LnOutboundSeeder.EligibilityGateByType[transactionType];
        var result = _svc.Evaluate(expr, inputJson);
        result.Ok.Should().BeTrue($"gate must evaluate: {result.Error}");
        // STRICT-TRUE (D-R9-6): only the literal true is eligible. Anything else — false, null, a non-boolean,
        // a missing field — fails closed.
        return result.OutputJson == "true";
    }

    // ── D16: PoAccept — monotonic on acceptedAt ───────────────────────────────────────────────────────

    [Fact]
    public void Accept_gate_passes_once_the_po_has_been_accepted()
        => Eligible(OutboxTransactionType.PoAccept,
            """{ "poStatus": "Accepted", "acceptedAt": "2026-07-29T09:00:00.0000000Z" }""").Should().BeTrue();

    [Fact]
    public void Accept_gate_fails_closed_when_the_po_was_never_accepted()
        => Eligible(OutboxTransactionType.PoAccept, """{ "poStatus": "Released" }""").Should().BeFalse();

    [Fact]
    public void Accept_gate_still_passes_after_the_po_has_moved_on()
    {
        // THE point of D16. A row that failed during the R12 cutover window gets re-armed days later, by which
        // time the PO may be PartiallyDelivered or FullyShipped. The dispatch-time re-check runs again — and a
        // `poStatus = "Accepted"` gate would return false and Skip the row TERMINALLY, so LN would never learn
        // the PO was accepted. acceptedAt cannot go stale that way.
        Eligible(OutboxTransactionType.PoAccept,
            """{ "poStatus": "FullyShipped", "acceptedAt": "2026-07-29T09:00:00.0000000Z" }""").Should().BeTrue();
    }

    // ── D16: PoReject — equality is already monotonic (Rejected is terminal) ──────────────────────────

    [Fact]
    public void Reject_gate_passes_on_a_rejected_po()
        => Eligible(OutboxTransactionType.PoReject, """{ "poStatus": "Rejected" }""").Should().BeTrue();

    [Fact]
    public void Reject_gate_fails_closed_on_any_other_status()
        => Eligible(OutboxTransactionType.PoReject, """{ "poStatus": "Accepted" }""").Should().BeFalse();

    // ── D16: PoNegotiationApprove ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Negotiation_gate_passes_on_an_approved_negotiation()
        => Eligible(OutboxTransactionType.PoNegotiationApprove,
            """{ "negotiationStatus": "Approved", "poNumber": "PUR000323" }""").Should().BeTrue();

    [Fact]
    public void Negotiation_gate_fails_closed_while_the_negotiation_is_still_open()
        => Eligible(OutboxTransactionType.PoNegotiationApprove,
            """{ "negotiationStatus": "Submitted" }""").Should().BeFalse();

    [Fact]
    public void Negotiation_gate_does_not_reference_the_wire_only_Negotiated_literal()
    {
        // Regression guard. "Negotiated" is the POStatus we SEND; no portal status ever equals it (approval
        // sets the PO to Approved and the negotiation to Approved). A gate using it would be strict-true
        // fail-closed on every row — every negotiation silently skipped, no error anywhere.
        LnOutboundSeeder.EligibilityGateByType[OutboxTransactionType.PoNegotiationApprove]
            .Should().NotContain("Negotiated");
    }

    [Fact]
    public void Every_po_update_transaction_ships_a_gate()
        => LnOutboundSeeder.EligibilityGateByType.Keys.Should().BeEquivalentTo(new[]
        {
            OutboxTransactionType.PoAccept,
            OutboxTransactionType.PoReject,
            OutboxTransactionType.PoNegotiationApprove,
        });

    // ── D4: the wire date format ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Wire_dates_are_seconds_precision_utc_with_an_explicit_Z()
    {
        var value = new DateTime(2026, 12, 29, 11, 0, 0, DateTimeKind.Utc);

        PoLineDocumentAssembler.FormatUtc(value).Should().Be("2026-12-29T11:00:00Z");
    }

    [Fact]
    public void An_unspecified_kind_is_treated_as_utc_not_local()
    {
        // Load-bearing, not cosmetic. SQL Server returns DateTimeKind.Unspecified and the portal stores UTC.
        // ToUniversalTime() on an Unspecified value would apply the SERVER's offset — so on any non-UTC
        // machine the wire value would silently shift, and LN would echo the shifted instant back looking
        // perfectly healthy.
        var fromDb = new DateTime(2026, 12, 29, 11, 0, 0, DateTimeKind.Unspecified);

        PoLineDocumentAssembler.FormatUtc(fromDb).Should().Be("2026-12-29T11:00:00Z");
    }

    [Fact]
    public void Wire_dates_carry_no_fractional_seconds()
    {
        // The repo's usual "o" round-trip format emits seven fractional digits; every observed LN sample has
        // none. Keeping the wire form identical to what the endpoint was proven against.
        var value = new DateTime(2026, 12, 29, 11, 0, 0, 123, DateTimeKind.Utc);

        PoLineDocumentAssembler.FormatUtc(value).Should().Be("2026-12-29T11:00:00Z");
        PoLineDocumentAssembler.FormatUtc(value).Should().NotContain(".");
    }

    [Fact]
    public void A_null_date_stays_null_rather_than_becoming_a_sentinel()
        => PoLineDocumentAssembler.FormatUtc(null).Should().BeNull();

    [Fact]
    public void The_format_is_culture_invariant()
    {
        // A culture with a non-Gregorian calendar or different digits would otherwise corrupt the wire value
        // on a machine whose locale happens to differ from the build agent's.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            PoLineDocumentAssembler.FormatUtc(new DateTime(2026, 12, 29, 11, 0, 0, DateTimeKind.Utc))
                .Should().Be("2026-12-29T11:00:00Z");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    // ── D7: the header-date agreement test ────────────────────────────────────────────────────────────

    private static PurchaseOrderLineInputDoc L(string? date) => new(10, 1, date, 5m, "EA");

    [Fact]
    public void Header_date_is_the_shared_value_when_every_line_agrees()
        => PoLineDocumentAssembler.HeaderDeliveryDate(new[] { L("2026-12-29T11:00:00Z"), L("2026-12-29T11:00:00Z") })
            .Should().Be("2026-12-29T11:00:00Z");

    [Fact]
    public void Header_date_is_null_when_lines_disagree()
        => PoLineDocumentAssembler.HeaderDeliveryDate(new[] { L("2026-12-29T11:00:00Z"), L("2026-12-30T11:00:00Z") })
            .Should().BeNull();

    [Fact]
    public void Header_date_is_null_when_any_line_has_none()
        => PoLineDocumentAssembler.HeaderDeliveryDate(new[] { L("2026-12-29T11:00:00Z"), L(null) })
            .Should().BeNull();

    [Fact]
    public void Header_date_is_null_when_the_first_line_has_none()
        => PoLineDocumentAssembler.HeaderDeliveryDate(new[] { L(null), L("2026-12-29T11:00:00Z") })
            .Should().BeNull();

    [Fact]
    public void Header_date_is_null_for_a_po_with_no_lines()
        => PoLineDocumentAssembler.HeaderDeliveryDate(Array.Empty<PurchaseOrderLineInputDoc>()).Should().BeNull();
}
