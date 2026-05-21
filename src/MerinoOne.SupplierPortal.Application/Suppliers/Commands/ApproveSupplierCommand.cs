using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

public record ApproveSupplierCommand(Guid SupplierId, ApproveSupplierRequest Body) : IRequest<Unit>;

public class ApproveSupplierCommandValidator : AbstractValidator<ApproveSupplierCommand> { /* business validation done in handler — needs DB */ }

public class ApproveSupplierCommandHandler : IRequestHandler<ApproveSupplierCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public ApproveSupplierCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(ApproveSupplierCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        // Latest verification per type (GST/PAN/MSME); fail-state requires override comment
        var latestByType = await _db.SupplierVerifications
            .Where(v => v.SupplierId == request.SupplierId)
            .GroupBy(v => v.VerificationType)
            .Select(g => g.OrderByDescending(v => v.AttemptedAt).First())
            .ToListAsync(ct);

        var anyFail = latestByType.Any(v => v.Result == VerificationResult.Fail);
        if (anyFail && string.IsNullOrWhiteSpace(request.Body.OverrideComment))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["approvalOverrideComment"] = new[] { "Override comment is required when any NIC verification is Fail." }
            });
        }

        supplier.RegistrationStatus = RegistrationStatus.Approved;
        supplier.IsActiveSupplier = true;
        supplier.ApprovedAt = DateTime.UtcNow;
        supplier.ApprovedBy = _user.UserCode;
        supplier.ApprovalOverrideComment = request.Body.OverrideComment;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
