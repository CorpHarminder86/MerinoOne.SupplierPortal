using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

// R17 (2026-10-26) — Carrier master admin update. Code is immutable (stable lookup key for ASN snapshots + LN upserts);
// description is optional (blank clears it) and capped at the 50-wide column.
public record UpdateCarrierCommand(Guid Id, UpdateCarrierRequest Body) : IRequest<CarrierDto>;

public class UpdateCarrierCommandValidator : AbstractValidator<UpdateCarrierCommand>
{
    public UpdateCarrierCommandValidator()
    {
        RuleFor(x => x.Body.Description)
            .Must(d => d is null || d.Trim().Length <= CreateCarrierCommandValidator.DescriptionMaxLength)
            .WithMessage($"'Description' must be {CreateCarrierCommandValidator.DescriptionMaxLength} characters or fewer.");
    }
}

public class UpdateCarrierCommandHandler : IRequestHandler<UpdateCarrierCommand, CarrierDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdateCarrierCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<CarrierDto> Handle(UpdateCarrierCommand request, CancellationToken ct)
    {
        var c = await _db.Carriers.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Carrier", request.Id);

        var description = request.Body.Description?.Trim();
        c.Description = string.IsNullOrEmpty(description) ? null : description;
        c.IsActive = request.Body.IsActive;
        c.UpdatedBy = _user.UserCode;
        c.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new CarrierDto(c.Id, c.Seq, c.Code, c.Description, c.IsActive, c.CreatedOn);
    }
}
