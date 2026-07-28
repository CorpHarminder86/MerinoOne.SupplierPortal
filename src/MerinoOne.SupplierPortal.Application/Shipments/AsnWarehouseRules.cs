using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R11 (D4) — the single-warehouse invariant for an ASN.
/// <para>An ASN is grouped by warehouse exactly as it is by ship-to: LN's
/// <c>whinh.advanceShipmentNotices</c> carries ONE receiving warehouse on the header, so an ASN whose lines
/// spanned two warehouses would be misrouted at the ERP. Every creation and update path resolves the warehouse
/// from the covered <see cref="Domain.Entities.Proc.PurchaseOrder.Warehouse"/> values and rejects a mixed
/// selection.</para>
/// <para>Nulls are deliberately NOT a distinct value. POs ingested before R11 carry a null warehouse (there was
/// no backfill), so treating null as its own group would block ASN flows that work today. Instead nulls are
/// ignored when deciding whether the selection is mixed, and an all-null selection resolves to null — the ASN
/// simply carries a null warehouse through to the LN payload, same as it does today.</para>
/// </summary>
public static class AsnWarehouseRules
{
    /// <summary>Error key used on the thrown <see cref="ValidationException"/>, mirroring the ship-to rule's.</summary>
    public const string ErrorKey = "warehouse";

    /// <summary>
    /// Returns the single warehouse shared by <paramref name="warehouses"/>, or null when none of them carries
    /// one. Throws a <see cref="ValidationException"/> naming the conflicting codes when two or more distinct
    /// non-null values are present.
    /// </summary>
    public static string? ResolveSingle(IEnumerable<string?> warehouses)
    {
        var distinct = warehouses
            .Select(w => w?.Trim())
            .Where(w => !string.IsNullOrEmpty(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count > 1)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [ErrorKey] = new[]
                {
                    "An ASN cannot mix warehouses; all selected purchase order lines must share one receiving " +
                    $"warehouse. Found: {string.Join(", ", distinct.OrderBy(w => w, StringComparer.OrdinalIgnoreCase))}."
                }
            });

        return distinct.Count == 1 ? distinct[0] : null;
    }
}
