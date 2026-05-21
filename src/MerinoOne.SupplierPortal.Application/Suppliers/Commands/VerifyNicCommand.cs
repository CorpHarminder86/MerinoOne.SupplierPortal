using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using SupplierVerification = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierVerification;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

public record VerifyNicCommand(Guid SupplierId, VerifyNicRequest Body) : IRequest<List<SupplierVerificationDto>>;

public class VerifyNicCommandValidator : AbstractValidator<VerifyNicCommand>
{
    public VerifyNicCommandValidator()
    {
        RuleFor(x => x.Body.Types).NotNull().NotEmpty()
            .Must(t => t.All(x => x == "GST" || x == "PAN" || x == "MSME"))
            .WithMessage("Types must be one of GST, PAN, MSME.");
    }
}

public class VerifyNicCommandHandler : IRequestHandler<VerifyNicCommand, List<SupplierVerificationDto>>
{
    private readonly IAppDbContext _db;
    private readonly INicValidationService _nic;
    private readonly ICurrentUser _user;

    public VerifyNicCommandHandler(IAppDbContext db, INicValidationService nic, ICurrentUser user)
    {
        _db = db;
        _nic = nic;
        _user = user;
    }

    public async Task<List<SupplierVerificationDto>> Handle(VerifyNicCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        var results = new List<SupplierVerificationDto>();
        foreach (var typeStr in request.Body.Types)
        {
            var type = Enum.Parse<VerificationType>(typeStr);
            var number = type switch
            {
                VerificationType.GST => supplier.GstNumber ?? string.Empty,
                VerificationType.PAN => supplier.PanNumber ?? string.Empty,
                VerificationType.MSME => supplier.MsmeRegNumber ?? string.Empty,
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(number)) continue;

            var outcome = await _nic.VerifyAsync(type, number, ct);

            var v = new SupplierVerification
            {
                Id = Guid.NewGuid(),
                SupplierId = supplier.Id,
                VerificationType = type,
                AttemptedAt = DateTime.UtcNow,
                AttemptedBy = _user.UserCode,
                ProviderName = outcome.Provider,
                Result = outcome.Result,
                ResponsePayload = outcome.RawPayload,
                Comments = outcome.Notes,
                SeccodeId = supplier.SeccodeId,
                CreatedBy = _user.UserCode,
                CreatedOn = DateTime.UtcNow,
            };
            _db.SupplierVerifications.Add(v);

            // Reflect latest on Supplier
            if (outcome.Result == VerificationResult.Pass)
            {
                if (type == VerificationType.GST) supplier.GstValidated = true;
                if (type == VerificationType.PAN) supplier.PanValidated = true;
                if (type == VerificationType.MSME) supplier.MsmeValidated = true;
            }
            else
            {
                if (type == VerificationType.GST) supplier.GstValidated = false;
                if (type == VerificationType.PAN) supplier.PanValidated = false;
                if (type == VerificationType.MSME) supplier.MsmeValidated = false;
            }

            results.Add(new SupplierVerificationDto(v.Id, type.ToString(), v.AttemptedAt,
                v.AttemptedBy, v.ProviderName, outcome.Result.ToString(), v.Comments));
        }

        await _db.SaveChangesAsync(ct);
        return results;
    }
}
