using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R11.1 — adapts an inbound ASN create/update REQUEST into <see cref="AsnCaptureUniqueness"/> captures and
/// throws a 400 on any clash. Shared by CreateAsnCommand and UpdateAsnCommand so the two enforce identically.
/// </summary>
internal static class AsnCaptureUniquenessSupport
{
    public static async Task ValidateRequestAsync(
        IAppDbContext db,
        Guid? excludeAsnId,
        Guid? companyId,
        IReadOnlyList<CreateAsnLineRequest> lines,
        IReadOnlyDictionary<Guid, PurchaseOrderLine> poLines,
        CancellationToken ct)
    {
        var serials = new List<AsnCaptureUniqueness.Capture>();
        var lots = new List<AsnCaptureUniqueness.Capture>();

        foreach (var line in lines)
        {
            // The ASN line carries no item of its own — the item comes from the source PO line. A line whose PO
            // line did not resolve is already rejected upstream, so skipping here cannot hide a clash.
            if (!poLines.TryGetValue(line.PurchaseOrderLineId, out var pol)) continue;
            var itemCode = pol.ItemCode;
            if (string.IsNullOrWhiteSpace(itemCode)) continue;

            foreach (var s in line.Serials ?? new List<AsnLineSerialInput>())
                if (!string.IsNullOrWhiteSpace(s.SerialNumber))
                    serials.Add(new AsnCaptureUniqueness.Capture(itemCode, s.SerialNumber.Trim()));

            foreach (var l in line.Lots ?? new List<AsnLineLotInput>())
                if (!string.IsNullOrWhiteSpace(l.LotNo))
                    lots.Add(new AsnCaptureUniqueness.Capture(itemCode, l.LotNo.Trim()));
        }

        if (serials.Count == 0 && lots.Count == 0) return;

        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string msg)
        {
            if (!errors.TryGetValue(key, out var list)) errors[key] = list = new List<string>();
            list.Add(msg);
        }

        await AsnCaptureUniqueness.ValidateAsync(db, excludeAsnId, companyId, serials, lots, Add, ct);

        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()));
    }
}
