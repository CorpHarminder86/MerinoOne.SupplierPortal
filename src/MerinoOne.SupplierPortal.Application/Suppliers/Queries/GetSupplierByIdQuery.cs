using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Queries;

public record GetSupplierByIdQuery(Guid Id) : IRequest<SupplierDetailDto>;

public class GetSupplierByIdQueryHandler : IRequestHandler<GetSupplierByIdQuery, SupplierDetailDto>
{
    private readonly IAppDbContext _db;
    public GetSupplierByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<SupplierDetailDto> Handle(GetSupplierByIdQuery request, CancellationToken ct)
    {
        var s = await _db.Suppliers
            .Include(x => x.Verifications)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Supplier", request.Id);

        return new SupplierDetailDto(
            s.Id, s.Seq, s.SupplierCode, s.LegalName, s.TradeName,
            s.SupplierType.ToString(),
            s.GstNumber, s.PanNumber, s.MsmeRegNumber, s.MsmeCategory,
            s.GstValidated, s.PanValidated, s.MsmeValidated,
            s.RegistrationStatus.ToString(),
            s.InvitedBy, s.InvitedAt, s.ApprovedBy, s.ApprovedAt,
            s.ApprovalOverrideComment, s.RejectionReason, s.Website,
            s.IsActiveSupplier,
            s.Verifications.OrderByDescending(v => v.AttemptedAt).Select(v =>
                new SupplierVerificationDto(v.Id, v.VerificationType.ToString(), v.AttemptedAt,
                    v.AttemptedBy, v.ProviderName, v.Result.ToString(), v.Comments)).ToList()
        );
    }
}
