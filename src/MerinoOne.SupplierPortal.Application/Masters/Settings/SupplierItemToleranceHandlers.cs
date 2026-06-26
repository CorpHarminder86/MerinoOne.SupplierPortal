using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Settings;

// ====================================================================================================
// R4 (2026-06-26) — Phase 5a, TSD R4 Addendum §7.4 (Component 4). The admin Settings CRUD surface for the
// supplier-item over-ship tolerance override grid. Admin-gated (Settings.Read / Settings.Write). The read
// LEFT-joins every active Item master to that supplier's SupplierItem override and exposes the RESOLVED
// tolerance via the same pure resolver the ASN guard uses (OverShipTolerance.ResolveOverShipTolerance) so the
// grid and the guard can never drift. Upsert by (supplierId, itemId); null tolerance clears to inherit.
// ====================================================================================================

// ---------------- GET ?supplierId= : the supplier's tolerance grid ----------------

public record GetSupplierItemTolerancesQuery(Guid SupplierId) : IRequest<List<SupplierItemToleranceDto>>;

public class GetSupplierItemTolerancesQueryHandler
    : IRequestHandler<GetSupplierItemTolerancesQuery, List<SupplierItemToleranceDto>>
{
    private readonly IAppDbContext _db;
    public GetSupplierItemTolerancesQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<SupplierItemToleranceDto>> Handle(GetSupplierItemTolerancesQuery request, CancellationToken ct)
    {
        var supplierExists = await _db.Suppliers.AnyAsync(s => s.Id == request.SupplierId, ct);
        if (!supplierExists) throw new NotFoundException("Supplier", request.SupplierId);

        // This supplier's overrides, keyed by item. Materialize once (small per-supplier set).
        var overrides = await _db.SupplierItems
            .Where(si => si.SupplierId == request.SupplierId)
            .Select(si => new { si.Id, si.ItemId, si.OverShipTolerancePct })
            .ToListAsync(ct);
        var overrideByItem = overrides.ToDictionary(o => o.ItemId);

        var items = await _db.Items
            .Where(i => i.IsActive)
            .OrderBy(i => i.Code)
            .Select(i => new { i.Id, i.Code, i.Description, i.OverShipTolerancePct })
            .ToListAsync(ct);

        var rows = new List<SupplierItemToleranceDto>(items.Count);
        foreach (var i in items)
        {
            overrideByItem.TryGetValue(i.Id, out var ov);
            // Resolve via the SAME pure resolver the guard uses (override ?? master). A present-but-NULL override
            // inherits the master; an absent override also inherits; an explicit value (incl. 0) wins.
            var siShadow = ov is null ? null : new SupplierItem { OverShipTolerancePct = ov.OverShipTolerancePct };
            var itemShadow = new Item { OverShipTolerancePct = i.OverShipTolerancePct };
            var resolved = OverShipTolerance.ResolveOverShipTolerance(siShadow, itemShadow);

            rows.Add(new SupplierItemToleranceDto(
                i.Id, i.Code, i.Description, i.OverShipTolerancePct,
                ov?.OverShipTolerancePct, resolved, ov?.Id));
        }
        return rows;
    }
}

// ---------------- PUT : upsert a supplier override (null = inherit) ----------------

public record UpsertSupplierItemToleranceCommand(UpsertSupplierItemToleranceRequest Body)
    : IRequest<SupplierItemToleranceDto>;

public class UpsertSupplierItemToleranceCommandValidator : AbstractValidator<UpsertSupplierItemToleranceCommand>
{
    public UpsertSupplierItemToleranceCommandValidator()
    {
        RuleFor(x => x.Body.SupplierId).NotEmpty();
        RuleFor(x => x.Body.ItemId).NotEmpty();
        // decimal(5,2) non-negative, range 0–999.99. Null is allowed (= inherit).
        RuleFor(x => x.Body.OverShipTolerancePct!.Value)
            .InclusiveBetween(0m, 999.99m)
            .When(x => x.Body.OverShipTolerancePct.HasValue)
            .WithName("OverShipTolerancePct")
            .WithMessage("Over-ship tolerance must be between 0 and 999.99.");
    }
}

public class UpsertSupplierItemToleranceCommandHandler
    : IRequestHandler<UpsertSupplierItemToleranceCommand, SupplierItemToleranceDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpsertSupplierItemToleranceCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<SupplierItemToleranceDto> Handle(UpsertSupplierItemToleranceCommand request, CancellationToken ct)
    {
        var b = request.Body;

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == b.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", b.SupplierId);
        var item = await _db.Items.FirstOrDefaultAsync(i => i.Id == b.ItemId, ct)
                   ?? throw new NotFoundException("Item", b.ItemId);

        var row = await _db.SupplierItems
            .FirstOrDefaultAsync(si => si.SupplierId == b.SupplierId && si.ItemId == b.ItemId, ct);

        if (row is null)
        {
            row = new SupplierItem
            {
                Id = Guid.NewGuid(),
                SupplierId = b.SupplierId,
                ItemId = b.ItemId,
                OverShipTolerancePct = b.OverShipTolerancePct,   // null = inherit (NULL stored, load-bearing).
                SeccodeId = supplier.SeccodeId,                  // Owner = supplier's G-seccode (seccode RLS).
                CreatedBy = _user.UserCode,
                CreatedOn = DateTime.UtcNow,
            };
            _db.SupplierItems.Add(row);
        }
        else
        {
            row.OverShipTolerancePct = b.OverShipTolerancePct;   // null clears to inherit.
            row.UpdatedBy = _user.UserCode;
            row.UpdatedOn = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);

        var siShadow = new SupplierItem { OverShipTolerancePct = row.OverShipTolerancePct };
        var resolved = OverShipTolerance.ResolveOverShipTolerance(siShadow, item);
        return new SupplierItemToleranceDto(
            item.Id, item.Code, item.Description, item.OverShipTolerancePct,
            row.OverShipTolerancePct, resolved, row.Id);
    }
}

// ---------------- DELETE : remove an override (revert to inherit) ----------------

public record DeleteSupplierItemToleranceCommand(Guid Id) : IRequest<Unit>;

public class DeleteSupplierItemToleranceCommandHandler : IRequestHandler<DeleteSupplierItemToleranceCommand, Unit>
{
    private readonly IAppDbContext _db;
    public DeleteSupplierItemToleranceCommandHandler(IAppDbContext db) => _db = db;

    public async Task<Unit> Handle(DeleteSupplierItemToleranceCommand request, CancellationToken ct)
    {
        var row = await _db.SupplierItems.FirstOrDefaultAsync(si => si.Id == request.Id, ct)
                  ?? throw new NotFoundException("SupplierItem", request.Id);
        _db.SupplierItems.Remove(row);   // soft-delete via the audit/soft-delete interceptor; filtered UQ frees the pair.
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
