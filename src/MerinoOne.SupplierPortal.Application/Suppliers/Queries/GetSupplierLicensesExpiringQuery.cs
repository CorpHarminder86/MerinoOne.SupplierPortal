using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Queries;

/// <summary>
/// Lists supplier licenses expiring within <paramref name="WithinDays"/> (default 90) for the expiry-reminder
/// dashboard. <see cref="Domain.Entities.Supplier.SupplierLicense"/> is a seccode-protected aggregate, so the
/// always-on seccode + company filters scope this to the caller automatically (suppliers see only their own).
/// The <c>IX_SupplierLicense_expiry</c> filtered index backs the date range scan.
/// </summary>
public record GetSupplierLicensesExpiringQuery(int WithinDays = 90) : IRequest<List<SupplierLicenseExpiringDto>>;

public class GetSupplierLicensesExpiringQueryHandler
    : IRequestHandler<GetSupplierLicensesExpiringQuery, List<SupplierLicenseExpiringDto>>
{
    private readonly IAppDbContext _db;
    public GetSupplierLicensesExpiringQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<SupplierLicenseExpiringDto>> Handle(GetSupplierLicensesExpiringQuery request, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cutoff = today.AddDays(Math.Max(0, request.WithinDays));

        var rows = await (
            from l in _db.SupplierLicenses
            where l.ExpiryDate != null && l.ExpiryDate <= cutoff
            join s in _db.Suppliers on l.SupplierId equals s.Id
            orderby l.ExpiryDate
            select new { l.Id, l.SupplierId, s.SupplierCode, s.LegalName, l.LicenseNumber, l.LicenseType, l.ExpiryDate })
            .ToListAsync(ct);

        return rows.Select(r => new SupplierLicenseExpiringDto(
            r.Id, r.SupplierId, r.SupplierCode, r.LegalName, r.LicenseNumber, r.LicenseType, r.ExpiryDate,
            r.ExpiryDate.HasValue ? r.ExpiryDate.Value.DayNumber - today.DayNumber : null)).ToList();
    }
}
