using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R13 (2026-08-05) — the single-warehouse invariant for an ASN, now sourced from the covered PO <b>lines</b>.
/// <para>Warehouse fully replaces the retired ship-to as the ASN grouping key: LN's
/// <c>whinh.advanceShipmentNotices</c> carries ONE receiving warehouse on the header, so an ASN whose lines
/// spanned two warehouses would be misrouted at the ERP. Warehouse is now a PER-LINE attribute
/// (<see cref="PurchaseOrderLine.Warehouse"/> code + <see cref="PurchaseOrderLine.WarehouseAddressId"/> +
/// <see cref="PurchaseOrderLine.WarehouseAddressSnapshot"/>) — the PO header no longer carries it — so every
/// creation and update path resolves the warehouse from the covered PO lines and rejects a mixed selection.</para>
/// <para>Nulls are deliberately NOT a distinct value. Lines ingested before R13 carry a null warehouse (there was
/// no backfill), so treating null as its own group would block ASN flows that work today. Instead nulls are
/// ignored when deciding whether the selection is mixed, and an all-null selection resolves to null — the ASN
/// simply carries a null warehouse through to the LN payload, same as it did before.</para>
/// </summary>
public static class AsnWarehouseRules
{
    /// <summary>Error key used on the thrown <see cref="ValidationException"/>, mirroring the retired ship-to rule's.</summary>
    public const string ErrorKey = "warehouse";

    /// <summary>One covered PO line's warehouse identity: the raw LN code, the resolved CompanyAddress FK, and the
    /// point-in-time snapshot frozen onto the line at inbound resolution. Any part may be null on a pre-R13 line.</summary>
    public sealed record WarehouseLine(string? Code, Guid? AddressId, WarehouseSnapshot? Snapshot);

    /// <summary>The single warehouse the ASN header carries: the code that ships to LN, the address FK for live
    /// linkage, and the snapshot the read side renders without a join. Any part may be null (all-null selection).</summary>
    public sealed record ResolvedWarehouse(string? Code, Guid? AddressId, WarehouseSnapshot? Snapshot);

    /// <summary>
    /// Resolves the single warehouse shared by the covered PO <paramref name="lines"/> — the code, the address FK,
    /// and a snapshot (copied from a line whose <see cref="WarehouseLine.Snapshot"/> is populated). Throws a
    /// <see cref="ValidationException"/> (key <see cref="ErrorKey"/>) when the lines carry two or more distinct
    /// non-null warehouse codes OR two or more distinct non-null address FKs. Nulls are ignored (see class note).
    /// </summary>
    public static ResolvedWarehouse Resolve(IEnumerable<WarehouseLine> lines)
    {
        var list = lines.ToList();
        var code = ResolveSingle(list.Select(l => l.Code));
        var addressId = ResolveSingleAddressId(list.Select(l => l.AddressId));

        // Prefer the snapshot off the line that matches the resolved address; fall back to any populated snapshot so
        // a line that resolved an address but (legacy) never froze a snapshot still yields whatever the set carries.
        WarehouseSnapshot? snapshot = null;
        if (addressId is Guid id)
            snapshot = list.FirstOrDefault(l => l.Snapshot is not null && l.AddressId == id)?.Snapshot;
        snapshot ??= list.FirstOrDefault(l => l.Snapshot is not null)?.Snapshot;

        return new ResolvedWarehouse(code, addressId, snapshot);
    }

    /// <summary>
    /// Returns the single warehouse CODE shared by <paramref name="warehouses"/>, or null when none of them carries
    /// one. Throws a <see cref="ValidationException"/> naming the conflicting codes when two or more distinct
    /// non-null values are present. Retained as a standalone helper (used by <see cref="Resolve"/> and any caller
    /// that only needs the code).
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

    /// <summary>
    /// Returns the single warehouse ADDRESS FK shared by <paramref name="ids"/>, or null when none is set. Throws a
    /// <see cref="ValidationException"/> (key <see cref="ErrorKey"/>) on two or more distinct non-empty values.
    /// </summary>
    private static Guid? ResolveSingleAddressId(IEnumerable<Guid?> ids)
    {
        var distinct = ids
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (distinct.Count > 1)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [ErrorKey] = new[]
                {
                    "An ASN cannot mix warehouses; all selected purchase order lines must share one receiving warehouse."
                }
            });

        return distinct.Count == 1 ? distinct[0] : (Guid?)null;
    }
}
