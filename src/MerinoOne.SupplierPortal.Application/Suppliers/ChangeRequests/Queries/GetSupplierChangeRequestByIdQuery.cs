using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Queries;

/// <summary>
/// Full change-request detail for the diff view: header + lines (each carrying the old→new diff for Edit, the
/// payloadJson for Add, the target id for Delete) + per-line push state (PushStatus / PushedAt / ErpRef). Lines
/// are loaded via the parent (<c>Include(r => r.Lines)</c>) — there is no root DbSet for lines. Seccode-scoped:
/// a supplier sees only its own request (404 otherwise); internal users see all.
/// </summary>
public record GetSupplierChangeRequestByIdQuery(Guid Id) : IRequest<SupplierChangeRequestDto>;

public class GetSupplierChangeRequestByIdQueryHandler
    : IRequestHandler<GetSupplierChangeRequestByIdQuery, SupplierChangeRequestDto>
{
    private readonly IAppDbContext _db;
    public GetSupplierChangeRequestByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<SupplierChangeRequestDto> Handle(GetSupplierChangeRequestByIdQuery request, CancellationToken ct)
    {
        var r = await _db.SupplierChangeRequests
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("SupplierChangeRequest", request.Id);

        var supplier = await _db.Suppliers
            .Select(s => new { s.Id, s.SupplierCode, s.LegalName })
            .FirstOrDefaultAsync(s => s.Id == r.SupplierId, ct);

        return SupplierChangeRequestMapper.ToDto(
            r,
            supplier?.SupplierCode ?? string.Empty,
            supplier?.LegalName ?? string.Empty);
    }
}
