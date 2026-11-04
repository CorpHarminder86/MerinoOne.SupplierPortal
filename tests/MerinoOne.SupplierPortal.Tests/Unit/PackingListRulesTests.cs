using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using QuestPDF.Infrastructure;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R18 — when an ASN's Packing List may be printed. <see cref="PackingListRules.IsAvailable"/> is the ONE rule behind the
/// API's 409 guard and the Web's button visibility, so it is pinned here as the full (status × buyer-confirmation) matrix.
/// Pure, DB-less.
/// </summary>
public class PackingListRulesTests
{
    // status → (printable when the supplier needs buyer confirmation, printable when it does not)
    private static readonly (AsnStatus Status, bool WhenRequired, bool WhenNotRequired)[] Matrix =
    {
        (AsnStatus.Draft,           false, true),    // a no-approval supplier prints before Post
        (AsnStatus.PendingApproval, false, false),   // the buyer is still reviewing
        (AsnStatus.Approved,        true,  false),   // confirmation granted — the supplier completes the shipment
        (AsnStatus.Rejected,        false, false),
        (AsnStatus.Submitted,       true,  true),    // posted to the ERP: every supplier
        (AsnStatus.InTransit,       true,  true),
        (AsnStatus.Delivered,       true,  true),
        (AsnStatus.Received,        true,  true),
        (AsnStatus.Cancelled,       false, false),
    };

    public static IEnumerable<object[]> MatrixRows
        => Matrix.Select(r => new object[] { r.Status.ToString(), r.WhenRequired, r.WhenNotRequired });

    [Theory]
    [MemberData(nameof(MatrixRows))]
    public void Availability_follows_the_status_and_the_suppliers_confirmation_mode(
        string status, bool whenRequired, bool whenNotRequired)
    {
        PackingListRules.IsAvailable(status, asnConfirmationRequired: true).Should().Be(whenRequired);
        PackingListRules.IsAvailable(status, asnConfirmationRequired: false).Should().Be(whenNotRequired);
    }

    [Fact]
    public void The_matrix_names_every_AsnStatus_so_a_new_status_forces_a_decision()
        => Matrix.Select(r => r.Status).Should().BeEquivalentTo(Enum.GetValues<AsnStatus>());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bogus")]
    [InlineData("draft")]       // matching is ordinal: the DTOs carry the enum NAME verbatim
    [InlineData("APPROVED")]
    [InlineData("Approved ")]   // and nothing is trimmed
    public void A_null_blank_unknown_or_mis_cased_status_is_never_available(string? status)
    {
        PackingListRules.IsAvailable(status!, asnConfirmationRequired: true).Should().BeFalse();
        PackingListRules.IsAvailable(status!, asnConfirmationRequired: false).Should().BeFalse();
    }
}

/// <summary>
/// R18 — how the Packing List writes its values (<see cref="PackingListFormat"/>): quantities, address lines, the carrier
/// label, the serial / lot / expiry columns and the remarks. Pure, DB-less. Each case guards a silent-wrongness failure —
/// a date attached to the wrong serial, a "-" built from a half-empty address — that no exception would ever flag.
/// </summary>
public class PackingListFormatTests
{
    // 12:00 UTC so the local calendar day is the same in every zone from UTC-12 to UTC+11.
    private static readonly DateTime LegacyExpiry = new(2027, 3, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Jan = new(2027, 1, 31);
    private static readonly DateOnly Mar = new(2027, 3, 31);

    private static AsnLineDto Line(
        IReadOnlyList<AsnLineSerialDto>? serials = null, IReadOnlyList<AsnLineLotDto>? lots = null,
        string? batch = null, DateTime? expiry = null, int decimals = 3)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "PO-1", 10, 10, 1, "ITM-1", "Item", "EA", 10m, 10m,
            batch, expiry, serials, lots, UnitDecimalPlaces: decimals);

    private static AsnLineSerialDto Serial(string number, DateOnly? expiry = null) => new(number, expiry, null);

    private static AsnLineLotDto Lot(string number, decimal qty, DateOnly? expiry = null) => new(number, qty, expiry, null);

    // ── Qty ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12.5", 3, "12.500")]
    [InlineData("7", 0, "7")]
    [InlineData("1234567.891", 2, "1234567.89")]   // never digit-grouped
    [InlineData("0", 4, "0.0000")]
    public void Qty_has_exactly_the_requested_decimals(string value, int decimals, string expected)
        => PackingListFormat.Qty(decimal.Parse(value, CultureInfo.InvariantCulture), decimals).Should().Be(expected);

    [Fact]
    public void Qty_ignores_the_host_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            PackingListFormat.Qty(1234.5m, 2).Should().Be("1234.50", because: "de-DE would write 1234,50");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── Pair / Carrier ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Acme Ltd", "Dock 4", "Acme Ltd – Dock 4")]
    [InlineData("Acme Ltd", null, "Acme Ltd")]
    [InlineData(null, "Dock 4", "Dock 4")]
    [InlineData("  ", " ", null)]
    public void Pair_joins_only_the_parts_that_exist(string? first, string? second, string? expected)
        => PackingListFormat.Pair(first, second).Should().Be(expected);

    [Theory]
    [InlineData("CAR-T1", "Test Carrier", "CAR-T1 – Test Carrier")]
    [InlineData("CAR-T1", "car-t1", "CAR-T1")]        // a master with no description snapshots the code as the name
    [InlineData("CAR-T1", null, "CAR-T1")]
    [InlineData("CAR-T1", "  ", "CAR-T1")]
    [InlineData(null, "Blue Dart", "Blue Dart")]      // pre-master ASN: the free-text name
    [InlineData("  ", "Blue Dart", "Blue Dart")]
    [InlineData(null, null, null)]
    public void Carrier_is_the_code_and_its_frozen_description(string? code, string? name, string? expected)
        => PackingListFormat.Carrier(code, name).Should().Be(expected);

    // ── AddressLines ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Address_is_line1_line2_area_then_one_city_pincode_state_country_line()
        => PackingListFormat.AddressLines(
                "12 MG Road", "Block B", "Andheri", "Mumbai", "400001", "Maharashtra", "India")
            .Should().Equal("12 MG Road", "Block B", "Andheri", "Mumbai - 400001, Maharashtra, India");

    [Fact]
    public void Stray_commas_and_whitespace_are_trimmed_from_every_part_but_inner_commas_stay()
        => PackingListFormat.AddressLines(
                ", 12 MG Road, ", " Unit 4, Phase 2 ,", null, " Mumbai,", ", 400001 ", "Maharashtra ,", " India,")
            .Should().Equal("12 MG Road", "Unit 4, Phase 2", "Mumbai - 400001, Maharashtra, India");

    [Fact]
    public void A_missing_part_leaves_no_stray_separator_behind()
    {
        PackingListFormat.AddressLines("Plot 5", null, null, "Pune", null, "Maharashtra", null)
            .Should().Equal("Plot 5", "Pune, Maharashtra");
        PackingListFormat.AddressLines("Plot 5", null, null, null, "411001", null, "India")
            .Should().Equal("Plot 5", "411001, India");
        PackingListFormat.AddressLines(null, " , ", "", null, null, null, null)
            .Should().BeEmpty(because: "nothing printable is left, so the caller prints its dash");
    }

    // ── SerialLotAndExpiry ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_plain_line_prints_its_batch_number_and_its_own_expiry_date()
    {
        var (entries, expiry) = PackingListFormat.SerialLotAndExpiry(Line(batch: " B-77 ", expiry: LegacyExpiry));

        entries.Should().Equal("B-77");
        expiry.Should().Be("2027-03-31");
    }

    [Fact]
    public void A_line_with_no_capture_no_batch_and_no_expiry_prints_nothing()
    {
        var (entries, expiry) = PackingListFormat.SerialLotAndExpiry(Line(batch: "   "));

        entries.Should().BeEmpty();
        expiry.Should().BeNull();
    }

    [Fact]
    public void Serials_are_one_entry_each_and_an_expiry_they_all_share_moves_to_the_expiry_column()
    {
        var (entries, expiry) = PackingListFormat.SerialLotAndExpiry(
            Line(serials: new[] { Serial("SN-1", Jan), Serial("SN-2", Jan) }));

        entries.Should().Equal("SN-1", "SN-2");
        expiry.Should().Be("2027-01-31");
    }

    [Fact]
    public void Serials_with_different_expiries_carry_their_own_date_and_leave_the_expiry_column_empty()
    {
        var (entries, expiry) = PackingListFormat.SerialLotAndExpiry(
            Line(serials: new[] { Serial("SN-1", Jan), Serial("SN-2", Mar) }));

        entries.Should().Equal("SN-1 · exp 2027-01-31", "SN-2 · exp 2027-03-31");
        expiry.Should().BeNull();
    }

    [Fact]
    public void A_serial_without_an_expiry_never_inherits_the_one_its_neighbour_has()
    {
        var (entries, expiry) = PackingListFormat.SerialLotAndExpiry(
            Line(serials: new[] { Serial("SN-1", Jan), Serial("SN-2") }));

        entries.Should().Equal("SN-1 · exp 2027-01-31", "SN-2");
        expiry.Should().BeNull(because: "a single date in the column would read as covering SN-2 too");
    }

    [Fact]
    public void Capture_without_any_expiry_falls_back_to_the_lines_own_expiry_date()
    {
        var (entries, expiry) = PackingListFormat.SerialLotAndExpiry(
            Line(serials: new[] { Serial("SN-1"), Serial("SN-2") }, expiry: LegacyExpiry));

        entries.Should().Equal("SN-1", "SN-2");
        expiry.Should().Be("2027-03-31");
    }

    [Fact]
    public void A_single_lot_prints_its_number_alone()
    {
        var (entries, expiry) = PackingListFormat.SerialLotAndExpiry(Line(lots: new[] { Lot("LOT-A", 10m, Jan) }));

        entries.Should().Equal("LOT-A");
        expiry.Should().Be("2027-01-31");
    }

    [Theory]
    [InlineData(2, "LOT-A (4.00)", "LOT-B (6.00)")]
    [InlineData(0, "LOT-A (4)", "LOT-B (6)")]
    public void Several_lots_print_their_quantity_at_the_lines_unit_decimals(int decimals, string first, string second)
    {
        var (entries, _) = PackingListFormat.SerialLotAndExpiry(
            Line(lots: new[] { Lot("LOT-A", 4m), Lot("LOT-B", 6m) }, decimals: decimals));

        entries.Should().Equal(first, second);
    }

    [Fact]
    public void Lots_with_different_expiries_carry_their_own_date_after_the_quantity()
    {
        var (entries, expiry) = PackingListFormat.SerialLotAndExpiry(
            Line(lots: new[] { Lot("LOT-A", 4m, Jan), Lot("LOT-B", 6m, Mar) }, decimals: 3));

        entries.Should().Equal("LOT-A (4.000) · exp 2027-01-31", "LOT-B (6.000) · exp 2027-03-31");
        expiry.Should().BeNull();
    }

    [Fact]
    public void Serials_win_over_lots_and_lots_win_over_the_legacy_batch()
    {
        PackingListFormat.SerialLotAndExpiry(Line(
                serials: new[] { Serial("SN-1") }, lots: new[] { Lot("LOT-A", 1m) }, batch: "B-1")).SerialLotNumbers
            .Should().Equal("SN-1");
        PackingListFormat.SerialLotAndExpiry(Line(lots: new[] { Lot("LOT-A", 1m) }, batch: "B-1")).SerialLotNumbers
            .Should().Equal("LOT-A");
    }

    // ── Remarks ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remarks_without_notes_is_just_the_standing_sentence()
        => PackingListFormat.Remarks("ASN-000123", null)
            .Should().Be("This packing list is generated based on ASN ASN-000123.");

    [Fact]
    public void Notes_follow_as_one_further_remark_with_their_line_breaks_folded()
        => PackingListFormat.Remarks("ASN-000123", "  Keep dry.\r\n\r\nDeliver   before noon.  ")
            .Should().Be("This packing list is generated based on ASN ASN-000123.\nKeep dry. Deliver before noon.");

    [Fact]
    public void Whitespace_only_notes_add_no_remark()
        => PackingListFormat.Remarks("ASN-000123", " \r\n ")
            .Should().Be("This packing list is generated based on ASN ASN-000123.");
}

/// <summary>
/// R18 — the QuestPDF renderer turns every shape of <see cref="PackingListDto"/> into a real PDF and never throws. A layout
/// exception would surface as a 500 on <c>GET api/asns/{id}/packing-list</c>, so the awkward shapes — nothing known, an
/// unbreakable 64-character IRN, hundreds of serial numbers on one line, more lines than fit a page — are rendered here.
/// DB-less: the host that sets the QuestPDF license in <c>AddInfrastructure</c> is not running, so the test sets it itself.
/// </summary>
public class PackingListPdfGeneratorTests
{
    private static readonly PackingListQuestPdfGenerator Generator = new();

    static PackingListPdfGeneratorTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly PackingListPartyDto Party = new(
        "Acme Textiles Private Limited",
        new[] { "Plot 14, MIDC Industrial Area", "Mumbai - 400093, Maharashtra, India" },
        "+91 98200 12345", "dispatch@acme.example", "27AAACA1234A1Z5", null);

    private static readonly PackingListPartyDto NoParty = new(null, Array.Empty<string>(), null, null, null, null);

    private static PackingListLineDto Line(int sl, IReadOnlyList<string>? serialLot = null)
        => new(sl, $"PO-{1000 + sl}", 10, null, 1, $"ITM-{sl:0000}", $"Item {sl} description", sl, 2, "EA",
            serialLot ?? Array.Empty<string>(), null);

    private static PackingListDto Dto(IReadOnlyList<PackingListLineDto> lines, string? irn = null) => new(
        Party, Party, Party, "PL-1", new DateTime(2026, 11, 3, 4, 30, 0, DateTimeKind.Utc), "ASN-000123",
        "PO-1001, PO-1002", "LN-ASN-1", "SUP-1", "INR", "FOB Mumbai", "CAR-T1 – Test Carrier", "MH12AB1234", "TRK-1",
        "291000123456", new DateOnly(2026, 11, 2), irn, new DateOnly(2026, 11, 1),
        lines, lines.Sum(l => l.ShippedQty), 2, "This packing list is generated based on ASN ASN-000123.",
        new DateTime(2026, 11, 3, 9, 15, 0, DateTimeKind.Utc));

    /// <summary>The page tree is written in the clear, so the pages are the "/Type /Page" objects (not "/Pages").</summary>
    private static int PageCount(byte[] pdf)
        => Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![s\w])").Count;

    private static byte[] RenderPdf(PackingListDto packingList)
    {
        var bytes = Generator.Generate(packingList);
        bytes.Length.Should().BeGreaterThan(1000, because: "a rendered A4 document is never a stub");
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
        return bytes;
    }

    [Fact]
    public void A_fully_populated_packing_list_fits_one_page_even_with_an_unbreakable_64_character_irn()
        => PageCount(RenderPdf(Dto(new[] { Line(1), Line(2, new[] { "SN-1", "SN-2" }) }, irn: new string('a', 64))))
            .Should().Be(1);

    [Fact]
    public void A_packing_list_with_no_value_known_and_no_lines_still_renders_one_page()
        => PageCount(RenderPdf(new PackingListDto(
                NoParty, NoParty, NoParty, null, null, "ASN-BLANK", null, null, null, null, null, null, null, null,
                null, null, null, null, Array.Empty<PackingListLineDto>(), 0m, 0, null, DateTime.UtcNow)))
            .Should().Be(1);

    [Fact]
    public void A_line_with_every_value_missing_still_renders()
        => RenderPdf(Dto(new[]
        {
            new PackingListLineDto(1, null, null, null, null, null, null, 0m, 0, null, Array.Empty<string>(), null),
        }));

    [Fact]
    public void One_line_with_hundreds_of_serial_numbers_flows_onto_further_pages_instead_of_failing()
        => PageCount(RenderPdf(Dto(new[]
            {
                Line(1, Enumerable.Range(1, 800).Select(i => $"SN-2026-{i:000000}").ToArray()),
            })))
            .Should().BeGreaterThan(1);

    [Fact]
    public void More_lines_than_fit_one_page_flow_onto_further_pages()
        => PageCount(RenderPdf(Dto(Enumerable.Range(1, 130).Select(i => Line(i)).ToList())))
            .Should().BeGreaterThan(2);
}
