using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Policies;

/// <summary>
/// R4 (2026-06-26) — TSD R4 Addendum §6.2 / §6.5, Component 3 (PO Confirmation Gate). Enforces
/// <see cref="PoConfirmationPolicy.AllowsShipping"/> per covered PO at ASN create AND submit, with the admin
/// override path (UC-PO-09):
/// <list type="number">
///   <item>If the gate ALLOWS shipping for that PO → no-op.</item>
///   <item>If the gate BLOCKS and a non-empty <c>overrideReason</c> is supplied AND the caller holds
///         <c>PurchaseOrder.OverrideGate</c> → proceed, writing an explicit PO-targeted audit row (op "Override",
///         actor/when/reason/PO/ASN) — mirrors the <c>PoNegotiationHistory</c> PO-targeted AuditEntry approach so
///         the PO "History" tab surfaces the exceptional shipment.</item>
///   <item>If the gate BLOCKS and the reason is empty OR the permission is absent → throw
///         <see cref="ValidationException"/> naming the required action (the normal hard block).</item>
/// </list>
/// Override WITHOUT a reason is rejected (an empty reason is treated as "no override requested").
/// </summary>
public static class PoConfirmationGateEnforcer
{
    // CK_AuditEntry_operation constrains operation to Insert/Update/Delete, so the override row uses "Update" and
    // stays identifiable by its "Gate override · …" FieldName prefix (mirrors PoNegotiationHistory's convention).
    private const string OverrideOp = "Update";
    private const int FieldMax = 100;   // audit.AuditEntry.fieldName is nvarchar(100).

    /// <summary>
    /// Evaluates the gate for every covered PO. <paramref name="asnId"/> is the (already-known) ASN id used to
    /// stamp the audit row; <paramref name="asnNumber"/> is the human label for the audit detail.
    /// </summary>
    public static void Enforce(
        IAppDbContext db,
        IReadOnlyCollection<PurchaseOrder> coveredPos,
        PoConfirmationMode mode,
        string? overrideReason,
        ICurrentUser user,
        Guid asnId,
        string asnNumber,
        DateTime now)
    {
        var hasReason = !string.IsNullOrWhiteSpace(overrideReason);
        var canOverride = user.HasPermission("PurchaseOrder.OverrideGate");
        var actor = string.IsNullOrEmpty(user.UserCode) ? "system" : user.UserCode;

        foreach (var po in coveredPos)
        {
            if (PoConfirmationPolicy.AllowsShipping(po.PoStatus, mode))
                continue;   // gate open for this PO.

            // Blocked. Admin override only when BOTH a non-empty reason AND the override permission are present.
            if (hasReason && canOverride)
            {
                db.AuditEntries.Add(new AuditEntry
                {
                    EntityName = nameof(PurchaseOrder),
                    EntityId = po.Id,
                    Operation = OverrideOp,
                    FieldName = Trunc($"Gate override · ASN {asnNumber}"),
                    OldValue = po.PoStatus.ToString(),                 // the blocked-at status
                    NewValue = $"ASN {asnId}: {overrideReason!.Trim()}",
                    ChangedBy = actor,
                    ChangedOn = now,
                    TenantId = po.TenantId,
                });
                continue;   // proceed for this PO under the audited override.
            }

            // Normal hard block — name the required action (§6.5).
            var action = PoConfirmationPolicy.RequiredAction(mode);
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["poStatus"] = new[] { $"PO {po.PoNumber} requires {action} before shipments can be created." }
            });
        }
    }

    private static string Trunc(string s) => s.Length > FieldMax ? s[..FieldMax] : s;
}
