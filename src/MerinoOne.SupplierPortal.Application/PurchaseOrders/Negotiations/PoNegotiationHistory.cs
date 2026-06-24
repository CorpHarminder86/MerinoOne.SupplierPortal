using System.Globalization;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations;

/// <summary>
/// Emits <see cref="AuditEntry"/> rows TARGETED AT THE PURCHASE ORDER (entityName = <c>PurchaseOrder</c>,
/// entityId = the PO id) for negotiation events, so the PO detail "History" tab — which queries the audit ledger
/// by <c>PurchaseOrder/{id}</c> — surfaces the proposed line changes (qty / delivery date) and the negotiation
/// outcome. The negotiation lives on its own aggregate (<see cref="PurchaseOrderNegotiation"/>), so its rows carry
/// a different entityName and would otherwise never reach the PO timeline.
///
/// <para>Rows are added to the change tracker so they commit in the SAME unit of work as the negotiation/PO
/// mutation. The <c>AuditableEntityInterceptor</c> only audits <c>AuditableEntity</c> instances and
/// <see cref="AuditEntry"/> is a plain <c>BaseEntity</c>, so this does NOT recurse. Id/Seq are DB-generated —
/// we mirror the interceptor and leave them unset.</para>
/// </summary>
internal static class PoNegotiationHistory
{
    // The audit ledger constrains operation to Insert/Update/Delete (CK_AuditEntry_operation), so these rows use
    // "Update"; the negotiation events stay identifiable by their "Negotiation · …" FieldName prefix.
    private const string Op = "Update";

    // audit.AuditEntry.fieldName is nvarchar(100).
    private const int FieldMax = 100;

    /// <summary>One row per CHANGED line (qty and/or delivery date), with the original → negotiated values.</summary>
    public static void RecordSubmitted(IAppDbContext db, PurchaseOrder po, PurchaseOrderNegotiation negotiation, string actor, DateTime now)
    {
        foreach (var line in negotiation.Lines)
        {
            var label = $"Negotiation · Line {line.PositionNo}/{line.SequenceNo} {line.ItemCode}".TrimEnd();

            if (line.OriginalQty != line.NegotiatedQty)
                db.AuditEntries.Add(Row(po, $"{label} · Qty", Num(line.OriginalQty), Num(line.NegotiatedQty), actor, now));

            if (line.OriginalDeliveryDate != line.NegotiatedDeliveryDate)
                db.AuditEntries.Add(Row(po, $"{label} · Delivery", Date(line.OriginalDeliveryDate), Date(line.NegotiatedDeliveryDate), actor, now));
        }
    }

    /// <summary>A single row marking the negotiation outcome (approved / rejected / cancelled); detail → New column.</summary>
    public static void RecordOutcome(IAppDbContext db, PurchaseOrder po, string outcome, string? detail, string actor, DateTime now)
        => db.AuditEntries.Add(Row(po, $"Negotiation {outcome}", null, string.IsNullOrWhiteSpace(detail) ? null : detail, actor, now));

    private static AuditEntry Row(PurchaseOrder po, string field, string? oldValue, string? newValue, string actor, DateTime now) => new()
    {
        EntityName = nameof(PurchaseOrder),
        EntityId = po.Id,
        Operation = Op,
        FieldName = field.Length > FieldMax ? field[..FieldMax] : field,
        OldValue = oldValue,
        NewValue = newValue,
        ChangedBy = actor,
        ChangedOn = now,
        TenantId = po.TenantId,
    };

    private static string Num(decimal d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "—";
}
