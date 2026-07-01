using System.Net;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R5 (TSD R5 Addendum §10.2) — shared helpers for the ASN approval flow: resolve the approver users + the
/// supplier-side recipient, and stage best-effort notification rows on <c>admin.emailOutbox</c> (mirrors the R4
/// EmailOutbox pattern — the row commits in the handler's own SaveChanges; the EmailOutboxWorker dispatches it).
///
/// <para><b>Approver resolution:</b> the INTERNAL, active portal users MAPPED to the ASN's supplier
/// (<c>admin.SupplierUserMap</c>) — so every buyer with hierarchy access to that supplier is notified and may
/// decide. (Was the PO's <c>BuyerUserId</c>, which nothing in the app populates.)</para>
/// </summary>
public static class AsnApprovalSupport
{
    private const string Actor = "asn-approval";

    /// <summary>
    /// The distinct AppUser ids of the INTERNAL, active portal users mapped to the ASN's supplier
    /// (<c>admin.SupplierUserMap</c>). IgnoreQueryFilters: the resolution runs under the approving/submitting
    /// principal, which may not hold the supplier's G-seccode.
    /// </summary>
    public static async Task<HashSet<Guid>> ResolveApproverUserIdsAsync(IAppDbContext db, Asn asn, CancellationToken ct)
    {
        var ids = await db.SupplierUserMaps.IgnoreQueryFilters()
            .Where(m => m.SupplierId == asn.SupplierId && !m.IsDeleted)
            .Join(db.AppUsers.IgnoreQueryFilters().Where(u => !u.IsDeleted && u.IsActive && u.IsInternal),
                m => m.AppUserId, u => u.Id, (m, u) => u.Id)
            .Distinct()
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    /// <summary>Covered PO ids: union of the lines' PO lines' POs, the junction rows, and the legacy scalar header PO.</summary>
    public static async Task<HashSet<Guid>> ResolveCoveredPoIdsAsync(IAppDbContext db, Asn asn, CancellationToken ct)
    {
        var viaLines = await db.AsnLines.IgnoreQueryFilters()
            .Where(al => al.AsnId == asn.Id && !al.IsDeleted)
            .Join(db.PurchaseOrderLines.IgnoreQueryFilters(), al => al.PurchaseOrderLineId, pol => pol.Id, (al, pol) => pol.PurchaseOrderId)
            .Distinct()
            .ToListAsync(ct);
        var viaJunction = await db.AsnPurchaseOrders.IgnoreQueryFilters()
            .Where(j => j.AsnId == asn.Id && !j.IsDeleted)
            .Select(j => j.PurchaseOrderId)
            .ToListAsync(ct);

        var set = viaLines.ToHashSet();
        foreach (var p in viaJunction) set.Add(p);
        if (asn.PurchaseOrderId.HasValue) set.Add(asn.PurchaseOrderId.Value);
        return set;
    }

    /// <summary>Best-effort: enqueue an approval-request e-mail to each resolved buyer with a non-blank active e-mail.</summary>
    public static async Task NotifyBuyersForApprovalAsync(
        IAppDbContext db, Asn asn, IReadOnlyCollection<Guid> buyerUserIds, DateTime now, CancellationToken ct)
    {
        if (buyerUserIds.Count == 0) return;
        var emails = await db.AppUsers.IgnoreQueryFilters()
            .Where(u => buyerUserIds.Contains(u.Id) && !u.IsDeleted && u.IsActive && u.Email != null && u.Email != "")
            .Select(u => u.Email)
            .Distinct()
            .ToListAsync(ct);

        foreach (var email in emails)
            db.EmailOutbox.Add(BuildOutbox(asn.TenantId, email!.Trim(),
                $"ASN {asn.AsnNumber} awaiting your approval",
                BuyerBody(asn.AsnNumber), now));
    }

    /// <summary>Best-effort: notify the supplier user who submitted the ASN that the buyer rejected it (with reason).</summary>
    public static async Task NotifySupplierRejectedAsync(
        IAppDbContext db, Asn asn, string submittedBy, string reason, DateTime now, CancellationToken ct)
    {
        var email = await db.AppUsers.IgnoreQueryFilters()
            .Where(u => u.UserCode == submittedBy && !u.IsDeleted && u.Email != null && u.Email != "")
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(email)) return;

        db.EmailOutbox.Add(BuildOutbox(asn.TenantId, email!.Trim(),
            $"ASN {asn.AsnNumber} was returned for changes",
            SupplierRejectedBody(asn.AsnNumber, reason), now));
    }

    /// <summary>R5 §20 — best-effort: notify the supplier user who submitted the ASN that the buyer APPROVED it
    /// (and it was submitted). Mirrors the reject notification; <paramref name="submittedBy"/> is the approval's
    /// SubmittedBy (the supplier user who sent it for approval).</summary>
    public static async Task NotifySupplierApprovedAsync(
        IAppDbContext db, Asn asn, string submittedBy, DateTime now, CancellationToken ct)
    {
        var email = await db.AppUsers.IgnoreQueryFilters()
            .Where(u => u.UserCode == submittedBy && !u.IsDeleted && u.Email != null && u.Email != "")
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(email)) return;

        db.EmailOutbox.Add(BuildOutbox(asn.TenantId, email!.Trim(),
            $"ASN {asn.AsnNumber} was approved",
            SupplierApprovedBody(asn.AsnNumber), now));
    }

    private static EmailOutbox BuildOutbox(Guid? tenantId, string to, string subject, string body, DateTime now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TemplateKey = "AsnApproval",
            ToEmail = to,
            Subject = subject,
            HtmlBody = body,
            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedBy = Actor,
            CreatedOn = now,
        };

    private static string BuyerBody(string asnNumber) => $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">ASN {WebUtility.HtmlEncode(asnNumber)} awaiting approval</h2>
  <p>A supplier has sent advance shipment notice <b>{WebUtility.HtmlEncode(asnNumber)}</b> for your approval.</p>
  <p>Please open the portal to review and approve or reject the shipment.</p>
</body></html>
""";

    private static string SupplierRejectedBody(string asnNumber, string reason) => $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">ASN {WebUtility.HtmlEncode(asnNumber)} returned for changes</h2>
  <p>Your advance shipment notice <b>{WebUtility.HtmlEncode(asnNumber)}</b> was rejected by the buyer and returned to Draft for editing.</p>
  <p><b>Reason:</b> {WebUtility.HtmlEncode(reason)}</p>
</body></html>
""";

    private static string SupplierApprovedBody(string asnNumber) => $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">ASN {WebUtility.HtmlEncode(asnNumber)} approved</h2>
  <p>Your advance shipment notice <b>{WebUtility.HtmlEncode(asnNumber)}</b> was approved by the buyer and has been submitted.</p>
  <p>No further action is required.</p>
</body></html>
""";
}
