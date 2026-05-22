using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices.Queries;

public record GetInvoiceByIdQuery(Guid Id) : IRequest<InvoiceDetailDto>;

public class GetInvoiceByIdQueryHandler : IRequestHandler<GetInvoiceByIdQuery, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    public GetInvoiceByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<InvoiceDetailDto> Handle(GetInvoiceByIdQuery request, CancellationToken ct)
    {
        var row = await (from inv in _db.Invoices
                         join po in _db.PurchaseOrders on inv.PurchaseOrderId equals po.Id
                         join s in _db.Suppliers on inv.SupplierId equals s.Id
                         where inv.Id == request.Id
                         select new
                         {
                             inv,
                             PoNumber = po.PoNumber,
                             SupplierName = s.LegalName,
                             SupplierCode = s.SupplierCode
                         }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Invoice", request.Id);

        string? asnNumber = null;
        if (row.inv.AsnId.HasValue)
        {
            asnNumber = await _db.Asns
                .Where(a => a.Id == row.inv.AsnId.Value)
                .Select(a => a.AsnNumber)
                .FirstOrDefaultAsync(ct);
        }

        var lines = await _db.InvoiceLines
            .Where(l => l.InvoiceId == request.Id)
            .Select(l => new InvoiceLineDto(
                l.Id,
                l.PurchaseOrderLineId,
                l.ItemCode,
                l.ItemDescription,
                l.BilledQty,
                l.UnitPrice,
                l.LineAmount,
                l.TaxCode,
                l.TaxAmount))
            .ToListAsync(ct);

        return new InvoiceDetailDto(
            row.inv.Id,
            row.inv.Seq,
            row.inv.InvoiceNumber,
            row.inv.PurchaseOrderId,
            row.PoNumber,
            row.inv.AsnId,
            asnNumber,
            row.inv.SupplierId,
            row.SupplierName,
            row.SupplierCode,
            row.inv.InvoiceDate,
            row.inv.InvoiceAmount,
            row.inv.TaxAmount,
            row.inv.NetAmount,
            row.inv.CurrencyCode,
            row.inv.MatchingType.ToString(),
            row.inv.GrnReference,
            row.inv.InvoiceStatus.ToString(),
            row.inv.RejectionReason,
            row.inv.EInvoiceIrn,
            row.inv.EInvoiceAckNo,
            row.inv.EWayBillNumber,
            row.inv.SubmittedBy,
            row.inv.ApprovedBy,
            row.inv.ApprovedAt,
            row.inv.Notes,
            row.inv.CreatedOn,
            lines);
    }
}
