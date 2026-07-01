using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

/// <summary>
/// R5 (review gap C2) — the approver-facing ASN approval queue: the ASNs awaiting a decision
/// (<see cref="AsnStatus.PendingApproval"/>). Scoped by the SUPPLIER-USER MAPPING (<c>admin.SupplierUserMap</c>):
/// an internal approver (the endpoint is policy-gated on <c>Asn.Approve</c>) sees the PendingApproval ASNs of the
/// suppliers they are mapped to — so multiple buyers with hierarchy access each see their own suppliers' ASNs.
/// (Per-buyer routing by <c>PurchaseOrder.BuyerUserId</c> was removed — nothing populates BuyerUserId.) An
/// <c>IsAdmin</c> caller oversees ALL PendingApproval ASNs in the tenant. A reviewer holds no supplier G-seccode,
/// so bypass the always-on seccode + active-company filters (<c>IgnoreQueryFilters</c>) and re-apply ONLY the
/// tenant predicate so the queue never crosses tenants.
///
/// Order: newest approval first — the latest Pending <c>AsnApproval.SubmittedOn</c> DESC.
/// </summary>
public record GetPendingAsnApprovalsQuery() : IRequest<List<AsnApprovalListItemDto>>;

public class GetPendingAsnApprovalsQueryHandler
    : IRequestHandler<GetPendingAsnApprovalsQuery, List<AsnApprovalListItemDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetPendingAsnApprovalsQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<AsnApprovalListItemDto>> Handle(GetPendingAsnApprovalsQuery request, CancellationToken ct)
    {
        // All PendingApproval ASNs in the tenant (seccode/company filters bypassed — a reviewer holds no G-seccode).
        var asns = _db.Asns.IgnoreQueryFilters()
            .Where(a => !a.IsDeleted
                        && a.TenantId == _user.TenantId
                        && a.AsnStatus == AsnStatus.PendingApproval);

        // Non-admins see ONLY the ASNs of the suppliers they are MAPPED to (admin.SupplierUserMap). Admins see all.
        if (!_user.IsAdmin)
        {
            var myUserId = await _db.AppUsers.IgnoreQueryFilters()
                .Where(u => u.UserCode == _user.UserCode && !u.IsDeleted)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(ct);

            // A null user id (unresolved principal) maps to nobody → empty queue.
            var mySupplierIds = _db.SupplierUserMaps.IgnoreQueryFilters()
                .Where(m => !m.IsDeleted && m.AppUserId == myUserId)
                .Select(m => m.SupplierId);

            asns = asns.Where(a => mySupplierIds.Contains(a.SupplierId));
        }

        // Suppliers stay tenant-scoped via IgnoreQueryFilters + tenant (the buyer holds no G-seccode).
        var sups = _db.Suppliers.IgnoreQueryFilters()
            .Where(s => !s.IsDeleted && s.TenantId == _user.TenantId);

        // The latest Pending approval session for each ASN — supplies SubmittedBy/On + the queue's sort key.
        var approvals = _db.AsnApprovals.IgnoreQueryFilters()
            .Where(ap => !ap.IsDeleted && ap.Status == AsnApprovalStatus.Pending);

        var rows = await (
            from a in asns
            join s in sups on a.SupplierId equals s.Id
            // The latest Pending approval for this ASN (there is exactly one live Pending session per ASN).
            let latest = approvals.Where(ap => ap.AsnId == a.Id)
                .OrderByDescending(ap => ap.SubmittedOn)
                .FirstOrDefault()
            orderby latest!.SubmittedOn descending
            select new
            {
                a.Id,
                a.Seq,
                a.AsnNumber,
                SupplierName = s.LegalName,
                a.ShipToAddressId,
                // Distinct covered POs = lines' POs ∪ junction POs ∪ legacy scalar header PO.
                PoIdsViaLines = _db.AsnLines.IgnoreQueryFilters()
                    .Where(al => al.AsnId == a.Id && !al.IsDeleted)
                    .Join(_db.PurchaseOrderLines.IgnoreQueryFilters(),
                        al => al.PurchaseOrderLineId, pol => pol.Id, (al, pol) => pol.PurchaseOrderId)
                    .Distinct()
                    .ToList(),
                PoIdsViaJunction = _db.AsnPurchaseOrders.IgnoreQueryFilters()
                    .Where(j => j.AsnId == a.Id && !j.IsDeleted)
                    .Select(j => j.PurchaseOrderId)
                    .Distinct()
                    .ToList(),
                LegacyPoId = a.PurchaseOrderId,
                SubmittedBy = latest != null ? latest.SubmittedBy : null,
                SubmittedOn = latest != null ? (DateTime?)latest.SubmittedOn : null,
            }).ToListAsync(ct);

        // Resolve the ship-to labels in one batch (avoids an N+1 join to CompanyAddress in the main projection).
        var shipToIds = rows.Where(r => r.ShipToAddressId.HasValue).Select(r => r.ShipToAddressId!.Value).Distinct().ToList();
        var shipToNames = shipToIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await _db.CompanyAddresses.IgnoreQueryFilters()
                .Where(ca => shipToIds.Contains(ca.Id))
                .Select(ca => new { ca.Id, ca.AddressName })
                .ToDictionaryAsync(x => x.Id, x => (string?)x.AddressName, ct);

        return rows.Select(r =>
        {
            var poCount = r.PoIdsViaLines.Concat(r.PoIdsViaJunction)
                .Concat(r.LegacyPoId.HasValue ? new[] { r.LegacyPoId.Value } : Array.Empty<Guid>())
                .Distinct()
                .Count();
            string? shipToName = r.ShipToAddressId.HasValue && shipToNames.TryGetValue(r.ShipToAddressId.Value, out var n)
                ? n : null;
            return new AsnApprovalListItemDto(
                r.Id, r.Seq, r.AsnNumber, r.SupplierName, shipToName, poCount, r.SubmittedBy, r.SubmittedOn);
        }).ToList();
    }
}
