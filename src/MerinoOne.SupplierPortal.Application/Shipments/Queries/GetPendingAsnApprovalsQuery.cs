using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

/// <summary>
/// R5 (review gap C2) — the buyer-facing ASN approval queue: the ASNs a buyer has been routed and that are
/// still awaiting a decision (<see cref="AsnStatus.PendingApproval"/>). Mirrors
/// <c>GetPoNegotiationListQuery</c>: a buyer/reviewer holds NO SecRight on the supplier G-seccodes, so the
/// always-on seccode + active-company filters would show them an empty queue. Bypass the filters
/// (<c>IgnoreQueryFilters</c>) and re-apply ONLY the tenant predicate (<c>TenantId == _user.TenantId</c>) so the
/// queue never crosses tenants. Suppliers are joined the same way (IgnoreQueryFilters + tenant) for the name.
///
/// <para><b>Routing (§10.2 parity with AsnApprovalSupport.ResolveApproverUserIdsAsync):</b> an ASN appears only
/// when at least one of its covered POs (via AsnLine → PurchaseOrderLine → PurchaseOrder, plus the junction and
/// the legacy scalar header PO) has <c>BuyerUserId == the current user's AppUser.Id</c>. If the caller
/// <c>IsAdmin</c>, ALL PendingApproval ASNs in the tenant are returned (admins oversee the whole queue).</para>
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
        // The current user's AppUser.Id (the value POs carry as BuyerUserId). IgnoreQueryFilters: the reviewer's
        // own row is a type-U principal, always resolvable regardless of seccode/company scope.
        var myUserId = await _db.AppUsers.IgnoreQueryFilters()
            .Where(u => u.UserCode == _user.UserCode && !u.IsDeleted)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

        // A buyer/reviewer holds no supplier G-seccode; bypass the seccode + active-company filters and re-apply
        // ONLY the tenant predicate so the queue never crosses tenants (mirror GetPoNegotiationListQuery).
        var asns = _db.Asns.IgnoreQueryFilters()
            .Where(a => !a.IsDeleted
                        && a.TenantId == _user.TenantId
                        && a.AsnStatus == AsnStatus.PendingApproval);

        // Non-admins see ONLY the ASNs routed to them: at least one covered PO whose BuyerUserId == my AppUser.Id.
        // Covered PO ids are resolved exactly as AsnApprovalSupport.ResolveCoveredPoIdsAsync does — the union of:
        //   • via lines: AsnLine → PurchaseOrderLine → PurchaseOrder
        //   • via the multi-PO junction: AsnPurchaseOrder
        //   • the legacy scalar header PO: Asn.PurchaseOrderId
        // A null user id (unresolved principal) can match nobody, so a non-admin gets an empty queue.
        if (!_user.IsAdmin)
        {
            var mine = myUserId;

            // POs owned by me (BuyerUserId == my AppUser.Id), tenant-scoped.
            var myPoIds = _db.PurchaseOrders.IgnoreQueryFilters()
                .Where(p => !p.IsDeleted && p.TenantId == _user.TenantId && p.BuyerUserId == mine)
                .Select(p => p.Id);

            asns = asns.Where(a =>
                // via lines
                _db.AsnLines.IgnoreQueryFilters()
                    .Where(al => al.AsnId == a.Id && !al.IsDeleted)
                    .Join(_db.PurchaseOrderLines.IgnoreQueryFilters(),
                        al => al.PurchaseOrderLineId, pol => pol.Id, (al, pol) => pol.PurchaseOrderId)
                    .Any(poId => myPoIds.Contains(poId))
                // via junction
                || _db.AsnPurchaseOrders.IgnoreQueryFilters()
                    .Where(j => j.AsnId == a.Id && !j.IsDeleted)
                    .Any(j => myPoIds.Contains(j.PurchaseOrderId))
                // legacy scalar header PO
                || (a.PurchaseOrderId != null && myPoIds.Contains(a.PurchaseOrderId.Value)));
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
