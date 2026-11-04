using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Contracts.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations;

/// <summary>
/// R18 (2026-11-03) — the per-line rules a negotiation proposal obeys, extracted from the create command so a supplier's
/// opening proposal and every counter-offer (either side's) are judged by the very same code. The rules need the PO and the
/// unit master, so they cannot sit in the DB-free FluentValidation validators: a handler builds one instance AFTER it has
/// authorised the caller (an unauthorised caller costs no extra query) and feeds it each proposed line.
///
/// <para>Every rule judges what the caller PROPOSES against a BASELINE the caller names — the live PO line for an opening
/// proposal, the effective values of the round being answered for a counter — so a value the caller merely re-sends untouched
/// is never judged. Failures are the field-keyed <see cref="ValidationException"/> (HTTP 400).</para>
/// </summary>
internal sealed class PoNegotiationLineRules
{
    private readonly PurchaseOrder _po;
    private readonly IReadOnlyDictionary<string, int> _places;
    private readonly int _fallbackPlaces;

    private PoNegotiationLineRules(
        PurchaseOrder po, IReadOnlyList<PurchaseOrderLine> lines, IReadOnlyDictionary<string, int> places, int fallbackPlaces)
    {
        _po = po; Lines = lines; _places = places; _fallbackPlaces = fallbackPlaces;
    }

    /// <summary>The PO's live (non-deleted) lines — the lines a proposal may touch. The PO must be loaded with its lines.</summary>
    public IReadOnlyList<PurchaseOrderLine> Lines { get; }

    /// <summary>
    /// Decimal places each of the PO's units allows — ONE batched lookup, read by the quantity-scale rule in <see cref="Judge"/>.
    /// </summary>
    public static async Task<PoNegotiationLineRules> ForAsync(PurchaseOrder po, IUnitDecimalsResolver units, CancellationToken ct)
    {
        var lines = po.Lines.Where(l => !l.IsDeleted).ToList();
        var places = await units.ResolveAsync(lines.Select(l => (string?)l.OrderUnit), ct);
        return new PoNegotiationLineRules(po, lines, places, units.Fallback);
    }

    /// <summary>The 400 for a problem with the request's lines as a whole rather than with one field of one line.</summary>
    public static ValidationException LinesInvalid(string message)
        => new(new Dictionary<string, string[]> { ["lines"] = new[] { message } });

    /// <summary>The PO line a request line names; 400 when it is not a (live) line of this PO.</summary>
    public PurchaseOrderLine Line(Guid purchaseOrderLineId)
        => Lines.FirstOrDefault(l => l.Id == purchaseOrderLineId)
           ?? throw LinesInvalid($"PO line {purchaseOrderLineId} does not belong to this PO.");

    /// <summary>
    /// Judges the line at <paramref name="index"/> of the request: what it proposes against <paramref name="baseline"/>.
    ///
    /// <para><b>Delivery floor</b> (feedback 2026-08-12 (3)) — a PROPOSED delivery instant may not predate the order itself. Both
    /// sides are UTC (PoDate arrives from LN in UTC; the client converts the picker's wall-clock time before POSTing), so this is
    /// a straight comparison. Enforced HERE and not in the validator because the rule needs the PO, and mirrored in the UI only
    /// as a courtesy — this is the authority.</para>
    ///
    /// <para>Gated on the date having changed against the baseline. The client re-sends the line's EXISTING delivery instant
    /// verbatim on a line where only the quantity moved, so validating every submitted line would reject a pure qty proposal
    /// because of a stored date the caller never touched — and one such line would fail the whole submission. The rule
    /// constrains what a party PROPOSES, not what the ERP already sent (or what the other party proposed).</para>
    ///
    /// <para><b>Quantity scale</b> — a PROPOSED quantity may not carry more decimal places than the line's unit allows. Gated on
    /// the quantity having changed for the same reason: the client re-sends an untouched quantity verbatim, and an ERP-fed order
    /// quantity is not guaranteed to fit the unit master, so judging every submitted line would fault a quantity the caller never
    /// edited. Keyed to the line's index in the REQUEST, not its position among the persisted delta lines. The 400 body carries
    /// only the message, never the key, so the message names the PO line (position/sequence) the way the delivery floor does.</para>
    /// </summary>
    public void Judge(PurchaseOrderLine poLine, int index, PoLineTerms baseline, PoLineTerms proposed)
    {
        if (baseline.DeliveryDate != proposed.DeliveryDate && proposed.DeliveryDate is { } delivery && delivery < _po.PoDate)
            throw LinesInvalid(
                $"Line {poLine.PositionNo}/{poLine.SequenceNo}: the delivery date and time " +
                $"({AppTime.Stamp(delivery)}) cannot be earlier than the PO date " +
                $"({AppTime.Stamp(_po.PoDate)}).");

        if (baseline.Qty == proposed.Qty) return;

        var places = PlacesFor(poLine.OrderUnit);
        if (HasMoreDecimalsThan(proposed.Qty, places))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [$"lines[{index}].negotiatedQty"] = new[]
                {
                    $"Quantity on line {poLine.PositionNo}/{poLine.SequenceNo} for unit " +
                    $"{UnitLabel(poLine.OrderUnit)} allows at most {places} decimal places."
                }
            });
    }

    /// <summary>
    /// The stored (delta) line for a PO line whose proposal differs from the live line: the live values are snapshotted as the
    /// original, the proposal as the negotiated side. The live PO line is NOT mutated — ERP stays the line master.
    /// </summary>
    public static PurchaseOrderNegotiationLine StoredLine(PurchaseOrderLine poLine, PoLineTerms proposed, string actor, DateTime now)
        => new()
        {
            Id = Guid.NewGuid(),
            PurchaseOrderLineId = poLine.Id,
            PositionNo = poLine.PositionNo,
            SequenceNo = poLine.SequenceNo,
            ItemCode = poLine.ItemCode,
            OriginalQty = poLine.OrderQty,
            NegotiatedQty = proposed.Qty,
            OriginalDeliveryDate = poLine.DeliveryDate,
            NegotiatedDeliveryDate = proposed.DeliveryDate,
            OriginalPrice = poLine.PriceUnit,
            NegotiatedPrice = proposed.Price,
            CreatedBy = actor,
            CreatedOn = now,
        };

    private int PlacesFor(string? unit)
        => !string.IsNullOrWhiteSpace(unit) && _places.TryGetValue(unit, out var known) ? known : _fallbackPlaces;

    /// <summary>
    /// True when <paramref name="value"/> has a non-zero digit beyond <paramref name="places"/> decimal places. Trailing
    /// zeros are not digits (1.50 fits a one-place unit, 1.55 does not), which is why this rounds and compares rather
    /// than reading the decimal's scale — a client binding "7.00" sends a scale-2 decimal that is still a whole number.
    /// A decimal holds at most 28 fractional digits, so a scale of 28 or more can never be exceeded.
    /// </summary>
    private static bool HasMoreDecimalsThan(decimal value, int places)
        => places is >= 0 and < 28 && decimal.Round(value, places) != value;

    private static string UnitLabel(string? unit) => string.IsNullOrWhiteSpace(unit) ? "(none)" : unit.Trim();
}
