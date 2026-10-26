using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

// R17 (2026-10-26) — Carrier master admin create. Cloned from CreateDeliveryTermCommand; the differences are the LN column
// widths (code <= 9, description <= 50) and that Description is OPTIONAL (LN can send a carrier with no name). The company is
// stamped by the ScopeStampInterceptor on add, exactly like DeliveryTerm.
public record CreateCarrierCommand(CreateCarrierRequest Body) : IRequest<CarrierDto>;

public class CreateCarrierCommandValidator : AbstractValidator<CreateCarrierCommand>
{
    public const int CodeMaxLength = 9;
    public const int DescriptionMaxLength = 50;

    public CreateCarrierCommandValidator()
    {
        // The handler stores the TRIMMED values, so the length rules apply to what will actually be persisted.
        RuleFor(x => x.Body.Code)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(c => c.Trim().Length <= CodeMaxLength)
            .WithMessage($"'Code' must be {CodeMaxLength} characters or fewer.");
        RuleFor(x => x.Body.Description)
            .Must(d => d is null || d.Trim().Length <= DescriptionMaxLength)
            .WithMessage($"'Description' must be {DescriptionMaxLength} characters or fewer.");
    }
}

public class CreateCarrierCommandHandler : IRequestHandler<CreateCarrierCommand, CarrierDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public CreateCarrierCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<CarrierDto> Handle(CreateCarrierCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        var exists = await _db.Carriers.AnyAsync(c => c.Code == code, ct);
        if (exists) throw new ConflictException($"Carrier with code '{code}' already exists.");

        var description = request.Body.Description?.Trim();
        var entity = new Carrier
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = string.IsNullOrEmpty(description) ? null : description,
            IsActive = request.Body.IsActive,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Carriers.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new CarrierDto(entity.Id, entity.Seq, entity.Code, entity.Description, entity.IsActive, entity.CreatedOn);
    }
}
