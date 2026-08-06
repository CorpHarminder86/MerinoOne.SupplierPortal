using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// R15 — validation for the optional ASN line → delivery-schedule back-links carried on
/// <see cref="CreateAsnLineRequest.DeliveryScheduleId"/> (the schedule-driven wizard sends one per line;
/// older callers omit them). Shared by Create + Update so both paths enforce the same rule:
/// a supplied id must reference a LIVE schedule of the SAME PO line — anything else is a 400, never a
/// silently-dropped link.
/// </summary>
internal static class AsnScheduleLinkSupport
{
    /// <summary>
    /// Asserts every non-empty <c>DeliveryScheduleId</c> on the request lines resolves to a live
    /// <c>DeliverySchedule</c> whose <c>PurchaseOrderLineId</c> matches the request line's. Throws a
    /// <see cref="ValidationException"/> keyed on <c>lines</c> otherwise. No-op for link-free requests.
    /// </summary>
    public static async Task ValidateAsync(IAppDbContext db, IEnumerable<CreateAsnLineRequest> lines, CancellationToken ct)
    {
        var links = lines
            .Where(l => l.DeliveryScheduleId is { } g && g != Guid.Empty)
            .Select(l => (ScheduleId: l.DeliveryScheduleId!.Value, l.PurchaseOrderLineId))
            .ToList();
        if (links.Count == 0) return;

        var ids = links.Select(x => x.ScheduleId).Distinct().ToList();
        var schedulePols = await db.DeliverySchedules
            .Where(s => ids.Contains(s.Id) && !s.IsDeleted)
            .Select(s => new { s.Id, s.PurchaseOrderLineId })
            .ToDictionaryAsync(s => s.Id, s => s.PurchaseOrderLineId, ct);

        var bad = links
            .Where(x => !schedulePols.TryGetValue(x.ScheduleId, out var pol) || pol != x.PurchaseOrderLineId)
            .Select(x => x.ScheduleId)
            .Distinct()
            .ToList();
        if (bad.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[]
                {
                    $"DeliveryScheduleId(s) not found or not on the referenced PO line: {string.Join(", ", bad)}"
                }
            });
    }

    /// <summary>Normalizes a request link for persistence (<c>Guid.Empty</c> → null).</summary>
    public static Guid? Normalize(Guid? deliveryScheduleId)
        => deliveryScheduleId is { } g && g != Guid.Empty ? g : null;
}
