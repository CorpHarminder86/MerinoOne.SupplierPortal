using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;

/// <summary>
/// Enhancement R4 — Module 9 (PO Schedule Calendar). Returns PO-line delivery dates inside a required
/// [From, To] window, grouped PO-wise per date. EF path only — seccode/company auto-applied for suppliers
/// (plan §3 Module 9). The window is mandatory and bounded server-side (validator) to cap the result set.
/// </summary>
public record GetPoScheduleCalendarQuery(
    DateTime From,
    DateTime To,
    Guid? SupplierId = null) : IRequest<IReadOnlyList<PoCalendarEventDto>>;

public class GetPoScheduleCalendarQueryValidator : AbstractValidator<GetPoScheduleCalendarQuery>
{
    // Cap the window so the calendar can't be turned into an unbounded scan.
    private const int MaxWindowDays = 366;

    public GetPoScheduleCalendarQueryValidator()
    {
        RuleFor(x => x.From).NotEmpty();
        RuleFor(x => x.To).NotEmpty();
        RuleFor(x => x).Must(x => x.To >= x.From)
            .WithMessage("'To' must be on or after 'From'.");
        RuleFor(x => x).Must(x => (x.To - x.From).TotalDays <= MaxWindowDays)
            .WithMessage($"Calendar window cannot exceed {MaxWindowDays} days.");
    }
}

public class GetPoScheduleCalendarQueryHandler : IRequestHandler<GetPoScheduleCalendarQuery, IReadOnlyList<PoCalendarEventDto>>
{
    private readonly IAppDbContext _db;
    public GetPoScheduleCalendarQueryHandler(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<PoCalendarEventDto>> Handle(GetPoScheduleCalendarQuery request, CancellationToken ct)
    {
        // Open POs with line delivery dates inside the window. PurchaseOrders is seccode-scoped.
        var q = from l in _db.PurchaseOrderLines
                join po in _db.PurchaseOrders on l.PurchaseOrderId equals po.Id
                join s in _db.Suppliers on po.SupplierId equals s.Id
                where l.DeliveryDate != null
                      && l.DeliveryDate >= request.From
                      && l.DeliveryDate <= request.To
                      && (po.PoStatus == PoStatus.Released
                          || po.PoStatus == PoStatus.Acknowledged
                          || po.PoStatus == PoStatus.Accepted
                          || po.PoStatus == PoStatus.DateProposed
                          || po.PoStatus == PoStatus.PartiallyDelivered)
                select new { po.Id, po.PoNumber, po.SupplierId, SupplierName = s.LegalName, l.DeliveryDate, l.ItemCode, l.OrderQty };

        if (request.SupplierId.HasValue)
            q = q.Where(x => x.SupplierId == request.SupplierId.Value);

        var flat = await q.ToListAsync(ct);

        // Group PO-wise per date: one event per (Date, PO), carrying that PO's lines for the date.
        var events = flat
            .GroupBy(x => new { Date = x.DeliveryDate!.Value.Date, x.Id, x.PoNumber, x.SupplierName })
            .Select(g => new PoCalendarEventDto(
                g.Key.Date,
                g.Key.PoNumber,
                g.Key.Id,
                g.Key.SupplierName,
                g.Select(x => new PoCalendarItemDto(x.ItemCode, x.OrderQty)).ToList()))
            .OrderBy(e => e.Date).ThenBy(e => e.PoNumber)
            .ToList();

        return events;
    }
}
