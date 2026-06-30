using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Contracts.Shipments;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

/// <summary>
/// R4 (2026-06-22) — Module 3. Returns the full multi-PO-aware ASN detail (covered-PO list, per-line PO context +
/// position/sequence, lifecycle fields, draft-invoice link, attachments). Delegates to <see cref="AsnDtoBuilder"/>
/// so every ASN handler returns an identical shape. The previous <c>?? Guid.Empty</c> PO shim is removed.
/// </summary>
public record GetAsnByIdQuery(Guid Id) : IRequest<AsnDetailDto>;

public class GetAsnByIdQueryHandler : IRequestHandler<GetAsnByIdQuery, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly IFulfilmentSettings _fulfilment;
    public GetAsnByIdQueryHandler(IAppDbContext db, IFulfilmentSettings fulfilment)
    {
        _db = db;
        _fulfilment = fulfilment;
    }

    public Task<AsnDetailDto> Handle(GetAsnByIdQuery request, CancellationToken ct)
        => AsnDtoBuilder.BuildAsync(_db, request.Id, ct, _fulfilment.OverShipAllowanceRounding);
}
