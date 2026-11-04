using System.Globalization;
using System.Linq.Expressions;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Common;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

/// <summary>
/// R18 (2026-11-03) — the read model behind <c>GET api/asns/{id}/packing-list</c>: everything the Packing List PDF
/// prints, resolved and formatted once so the renderer does no lookups.
///
/// <para>The ASN, its lines and its covered POs come from <see cref="AsnDtoBuilder"/> (no second read of the lines);
/// the rest is a fixed number of small reads — supplier master, the supplier's addresses and contacts, the covered POs'
/// delivery terms, the customer company and the receiving warehouse — none of them per line. Visibility is exactly the
/// ASN detail's: an ASN that does not exist or that the caller's seccode scope hides is a 404 (decided BEFORE
/// availability, so a caller outside the scope cannot probe an ASN's status). Availability is enforced HERE, in the one
/// place both the endpoint and any other consumer pass through: an ASN whose status
/// <see cref="PackingListRules.IsAvailable"/> rejects is a 409.</para>
///
/// <para>Carrier and ship-to are read from what the ASN froze at write time (<c>CarrierCode</c> + <c>CarrierName</c>,
/// the warehouse snapshot), not re-joined to the masters — a later LN re-sync of a carrier or an edit of a warehouse
/// must not rewrite a document that already describes a shipment.</para>
/// </summary>
public record GetPackingListQuery(Guid AsnId) : IRequest<PackingListDto>;

public class GetPackingListQueryHandler : IRequestHandler<GetPackingListQuery, PackingListDto>
{
    // The SupplierAddress.AddressType values the portal itself produces (registration form + change-request editor):
    // Registered / Billing / Shipping / Other. The header prints the Registered address, Ship From the Shipping one.
    private const string RegisteredAddressType = "Registered";
    private const string ShippingAddressType = "Shipping";

    private readonly IAppDbContext _db;
    public GetPackingListQueryHandler(IAppDbContext db) => _db = db;

    private sealed record SupplierAddressRow(
        string AddressType, string? Line1, string? Line2, string? Area, string? City, string? Pincode, string? State, string? Country)
    {
        public IReadOnlyList<string> Lines => PackingListFormat.AddressLines(Line1, Line2, Area, City, Pincode, State, Country);
    }

    public async Task<PackingListDto> Handle(GetPackingListQuery request, CancellationToken ct)
    {
        var asn = await AsnDtoBuilder.BuildAsync(_db, request.AsnId, ct);

        if (!PackingListRules.IsAvailable(asn.AsnStatus, asn.AsnConfirmationRequired))
            throw new ConflictException(asn.AsnConfirmationRequired
                ? $"The Packing List is not available: ASN {asn.AsnNumber} is '{asn.AsnStatus}'. For a supplier that needs buyer confirmation it can be printed once the ASN is Approved, and after it is posted."
                : $"The Packing List is not available: ASN {asn.AsnNumber} is '{asn.AsnStatus}'. For a supplier that needs no buyer confirmation it can be printed from Draft, and after the ASN is posted.");

        // The customer company and the frozen receiving-warehouse snapshot live on the ASN row, not on the DTO.
        var header = await _db.Asns.AsNoTracking().FirstAsync(a => a.Id == asn.Id, ct);

        var supplier = await _db.Suppliers.AsNoTracking().Where(s => s.Id == asn.SupplierId)
            .Select(s => new { s.SupplierCode, s.GstNumber, s.DeliveryTermCode })
            .FirstOrDefaultAsync(ct);

        var addresses = await _db.SupplierAddresses.AsNoTracking()
            .Where(a => a.SupplierId == asn.SupplierId)
            .OrderBy(a => a.Seq)
            .Select(a => new SupplierAddressRow(
                a.AddressType, a.AddressLine1, a.AddressLine2, a.Area, a.City, a.Pincode, a.State, a.Country))
            .ToListAsync(ct);

        // The primary contact, else the first one (IsPrimary DESC, then creation order).
        var contact = await _db.SupplierContacts.AsNoTracking()
            .Where(c => c.SupplierId == asn.SupplierId)
            .OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Seq)
            .Select(c => new { c.Phone, c.Email })
            .FirstOrDefaultAsync(ct);

        var coveredPoIds = asn.PurchaseOrders.Select(p => p.PurchaseOrderId).ToList();
        var poDeliveryTerms = await _db.PurchaseOrders.AsNoTracking()
            .Where(p => coveredPoIds.Contains(p.Id))
            .Select(p => new { p.Id, p.DeliveryTerms })
            .ToDictionaryAsync(p => p.Id, p => p.DeliveryTerms, ct);

        var (warehouseName, shipToLines) = await ResolveWarehouseAsync(header, ct);
        var companyName = header.TenantEntityId is { } companyId
            ? await _db.TenantEntities.AsNoTracking().Where(e => e.Id == companyId).Select(e => e.Name).FirstOrDefaultAsync(ct)
            : null;

        // Supplier header: the Registered address, else the first one. Ship From: the Shipping address, else the header's.
        var headerAddress = addresses.FirstOrDefault(a => IsType(a, RegisteredAddressType)) ?? addresses.FirstOrDefault();
        var shipFromAddress = addresses.FirstOrDefault(a => IsType(a, ShippingAddressType)) ?? headerAddress;
        var supplierName = PackingListFormat.Blank(asn.SupplierName);

        var lines = asn.Lines.Select((l, i) =>
        {
            var (serialLotNumbers, expiryDate) = PackingListFormat.SerialLotAndExpiry(l);
            return new PackingListLineDto(
                i + 1, l.PoNumber, l.PositionNo ?? l.PoPositionNo, null, l.SequenceNo,
                l.ItemCode, l.ItemDescription, l.ShippedQty, l.UnitDecimalPlaces, l.OrderUnit,
                serialLotNumbers, expiryDate);
        }).ToList();

        return new PackingListDto(
            Supplier: new PackingListPartyDto(
                supplierName, headerAddress?.Lines ?? [], contact?.Phone, contact?.Email, supplier?.GstNumber, Cin: null),
            ShipFrom: new PackingListPartyDto(
                supplierName, shipFromAddress?.Lines ?? [], null, null, null, null),
            ShipTo: new PackingListPartyDto(
                PackingListFormat.Pair(companyName, warehouseName), shipToLines, null, null, null, null),
            PackingListNumber: asn.PackingList,
            PackingListDate: asn.PostedAt ?? asn.CreatedOn,
            AsnNumber: asn.AsnNumber,
            PurchaseOrders: PackingListFormat.Blank(string.Join(", ", asn.PurchaseOrders.Select(p => p.PoNumber).Distinct())),
            ShipmentNumber: asn.ErpCode,
            SupplierCode: supplier?.SupplierCode,
            Currency: asn.PurchaseOrders.Select(p => p.CurrencyCode).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
            DeliveryTerms: asn.PurchaseOrders.Select(p => poDeliveryTerms.GetValueOrDefault(p.PurchaseOrderId))
                               .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                           ?? supplier?.DeliveryTermCode,
            Carrier: PackingListFormat.Carrier(asn.CarrierCode, asn.CarrierName),
            VehicleNo: asn.VehicleNumber,
            TrackingNo: asn.TrackingNumber,
            EwayBillNo: asn.EwayBillNo,
            EwayBillDate: asn.EwayBillDate,
            IrnNo: asn.IrnNo,
            IrnDate: asn.IrnDate,
            Lines: lines,
            TotalShippedQty: lines.Sum(l => l.ShippedQty),
            TotalQtyDecimals: lines.Count == 0 ? 0 : lines.Max(l => l.QtyDecimals),
            Remarks: PackingListFormat.Remarks(asn.AsnNumber, asn.Notes),
            GeneratedAt: AppTime.UtcNow);
    }

    private static bool IsType(SupplierAddressRow address, string type)
        => string.Equals(address.AddressType, type, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The receiving warehouse's name and address lines: the ASN's frozen snapshot first, else the live
    /// <c>CompanyAddress</c> the ASN points at, else (address only) the company's base address. A source counts only when
    /// it yields at least one address line; the warehouse NAME is the first one any of the first two sources carries — a base
    /// address is the company's own, not a warehouse, so it never contributes one.
    /// </summary>
    private async Task<(string? Name, IReadOnlyList<string> Lines)> ResolveWarehouseAsync(Asn header, CancellationToken ct)
    {
        var snapshot = header.WarehouseAddressSnapshot;
        var name = snapshot?.AddressName;
        IReadOnlyList<string> lines = snapshot is null
            ? []
            : PackingListFormat.AddressLines(
                snapshot.Line1, snapshot.Line2, null, snapshot.City, snapshot.Pincode, snapshot.State, snapshot.Country);

        if (lines.Count == 0 && header.WarehouseAddressId is { } warehouseId)
        {
            var live = await CompanyAddressAsync(a => a.Id == warehouseId, ct);
            name ??= live.Name;
            lines = live.Lines;
        }

        if (lines.Count == 0 && header.TenantEntityId is { } companyId)
            lines = (await CompanyAddressAsync(a => a.TenantEntityId == companyId && a.IsBaseAddress, ct)).Lines;

        return (name, lines);
    }

    private async Task<(string? Name, IReadOnlyList<string> Lines)> CompanyAddressAsync(
        Expression<Func<CompanyAddress, bool>> predicate, CancellationToken ct)
    {
        var address = await _db.CompanyAddresses.AsNoTracking().Where(predicate)
            .Select(a => new { a.AddressName, a.AddressLine1, a.AddressLine2, a.City, a.Pincode, a.State, a.Country })
            .FirstOrDefaultAsync(ct);

        return address is null
            ? (null, [])
            : (address.AddressName,
               PackingListFormat.AddressLines(
                   address.AddressLine1, address.AddressLine2, null, address.City, address.Pincode, address.State, address.Country));
    }
}

/// <summary>
/// R18 (2026-11-03) — how the Packing List writes its values, in one place so the read model and the PDF renderer cannot
/// disagree. All pure: nothing here touches the database, the clock or the culture.
/// </summary>
public static class PackingListFormat
{
    /// <summary>Joins two parts of one label ("CODE – Description", "Company – Warehouse").</summary>
    public const string Separator = " – ";

    /// <summary>The value with surrounding whitespace removed; null when nothing is left.</summary>
    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The parts that have a value, joined by <see cref="Separator"/>; null when none does.</summary>
    public static string? Pair(string? first, string? second) => Join(Separator, Blank(first), Blank(second));

    /// <summary>
    /// A quantity with exactly <paramref name="decimals"/> decimal places and the invariant culture (no grouping), so the
    /// shipped quantity and the "LOTNO (qty)" entries of one line are written the same way on any host.
    /// </summary>
    public static string Qty(decimal value, int decimals)
        => value.ToString("F" + Math.Clamp(decimals, 0, 28), CultureInfo.InvariantCulture);

    /// <summary>
    /// "CODE – Description" for a carrier picked from the Carrier master. The ASN snapshots the master's description (or
    /// the code itself when the master has none) into <paramref name="name"/> at write time, so a name equal to the code
    /// adds nothing and prints the code alone; an ASN with no code (it predates the master) prints its free-text name.
    /// </summary>
    public static string? Carrier(string? code, string? name)
        => Pair(code, string.Equals(Blank(code), Blank(name), StringComparison.OrdinalIgnoreCase) ? null : name);

    /// <summary>
    /// Address as printable lines: line 1, line 2, area, then one "City - Pincode, State, Country" line built from
    /// whichever of those parts exist. Stray leading / trailing commas and whitespace — a known artefact of ERP address
    /// data — are trimmed from every part.
    /// </summary>
    public static IReadOnlyList<string> AddressLines(
        string? line1, string? line2, string? area, string? city, string? pincode, string? state, string? country)
    {
        var cityAndPin = Join(" - ", Tidy(city), Tidy(pincode));
        var region = Join(", ", cityAndPin, Tidy(state), Tidy(country));
        return new[] { Tidy(line1), Tidy(line2), Tidy(area), region }.OfType<string>().ToList();
    }

    /// <summary>
    /// The serial / lot column and the expiry column of one ASN line.
    ///
    /// <para><b>Entries</b>: each serial number; else the lots — a single lot as "LOTNO", several as "LOTNO (qty)" (the
    /// quantity at the line's unit decimals); else the legacy batch number.</para>
    ///
    /// <para><b>Expiry</b>: when every serial / lot carries the same expiry that date goes in the expiry column and the
    /// entries stay plain; when they carry different ones (or only some do) the column stays empty and each entry that
    /// has one gets " · exp yyyy-MM-dd" appended instead, so no date is ever attached to the wrong entry; when none carries
    /// one the line's own (legacy) expiry date is used.</para>
    /// </summary>
    public static (IReadOnlyList<string> SerialLotNumbers, string? ExpiryDate) SerialLotAndExpiry(AsnLineDto line)
    {
        List<(string Text, DateOnly? Expiry)> entries;
        if (line.Serials is { Count: > 0 } serials)
            entries = serials.Select(s => (s.SerialNumber, s.ExpiryDate)).ToList();
        else if (line.Lots is { Count: > 0 } lots)
            entries = lots.Select(l => (lots.Count == 1 ? l.LotNo : $"{l.LotNo} ({Qty(l.Qty, line.UnitDecimalPlaces)})", l.ExpiryDate)).ToList();
        else
            entries = Blank(line.BatchNumber) is { } batch ? [(batch, null)] : [];

        var texts = entries.Select(e => e.Text).ToList();
        var carried = entries.Select(e => e.Expiry).OfType<DateOnly>().Distinct().ToList();

        if (carried.Count == 0)
            return (texts, line.ExpiryDate is { } legacy ? AppTime.Date(legacy) : null);
        if (carried.Count == 1 && entries.All(e => e.Expiry is not null))
            return (texts, Day(carried[0]));

        var annotated = entries.Select(e => e.Expiry is { } expiry ? $"{e.Text} · exp {Day(expiry)}" : e.Text).ToList();
        return (annotated, null);
    }

    /// <summary>
    /// The Packing List's remarks, one numbered remark per line: the standing "generated based on ASN" sentence, then the
    /// ASN's notes (its line breaks folded to single spaces so the notes stay ONE remark) when it has any.
    /// </summary>
    public static string Remarks(string asnNumber, string? notes)
    {
        var generated = $"This packing list is generated based on ASN {asnNumber}.";
        var oneLine = string.IsNullOrWhiteSpace(notes)
            ? null
            : string.Join(' ', notes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine is null ? generated : generated + "\n" + oneLine;
    }

    private static string Day(DateOnly date) => date.ToString(AppTime.DateFormat, CultureInfo.InvariantCulture);

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        return present.Length == 0 ? null : string.Join(separator, present);
    }

    /// <summary>Strips whitespace and commas from both ends (never from the middle); null when nothing is left.</summary>
    private static string? Tidy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var span = value.AsSpan();
        while (span.Length > 0 && (char.IsWhiteSpace(span[0]) || span[0] == ',')) span = span[1..];
        while (span.Length > 0 && (char.IsWhiteSpace(span[^1]) || span[^1] == ',')) span = span[..^1];
        return span.Length == 0 ? null : span.ToString();
    }
}
