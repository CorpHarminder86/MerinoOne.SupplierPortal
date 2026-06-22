using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

/// <summary>Updates a Tax master row (code immutable to keep FK lookups stable, mirroring DeliveryTerm).</summary>
public record UpdateTaxCommand(Guid Id, UpdateTaxRequest Body) : IRequest<TaxDto>;

public class UpdateTaxCommandValidator : AbstractValidator<UpdateTaxCommand>
{
    public UpdateTaxCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.TaxRate).GreaterThanOrEqualTo(0).When(x => x.Body.TaxRate.HasValue);
    }
}

public class UpdateTaxCommandHandler : IRequestHandler<UpdateTaxCommand, TaxDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdateTaxCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<TaxDto> Handle(UpdateTaxCommand request, CancellationToken ct)
    {
        var t = await _db.Taxes.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Tax", request.Id);

        t.Description = request.Body.Description.Trim();
        t.TaxRate = request.Body.TaxRate;
        t.IsActive = request.Body.IsActive;
        t.UpdatedBy = _user.UserCode;
        t.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new TaxDto(t.Id, t.Seq, t.Code, t.Description, t.TaxRate, t.IsActive, t.CreatedOn);
    }
}
