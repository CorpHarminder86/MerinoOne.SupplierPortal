using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

/// <summary>
/// Updates a Tax master row (code immutable to keep FK lookups stable, mirroring DeliveryTerm).
/// R6 (2026-07-02) — a TaxRate VALUE change pins the rate (<c>IsRateOverridden = true</c>; the LN tax sync then
/// skips writing TaxRate). <c>ResetRateOverride = true</c> clears the pin and restores
/// <c>TaxRate = LastSyncedRate</c>.
/// </summary>
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
        if (request.Body.ResetRateOverride)
        {
            // Nothing to restore — a never-synced tax has no LastSyncedRate; "resetting" would silently NULL a
            // live rate (data loss). 400 instead.
            if (t.LastSyncedRate is null)
                throw new Common.Exceptions.ValidationException(new Dictionary<string, string[]>
                {
                    ["resetRateOverride"] = new[] { "No synced rate to restore for this tax." }
                });

            // Clear the admin pin: the rate snaps back to the last LN-synced value and the sync owns it again.
            t.IsRateOverridden = false;
            t.TaxRate = t.LastSyncedRate;
        }
        else
        {
            // A VALUE change pins the rate against the LN sync (an unchanged rate does not).
            if (t.TaxRate != request.Body.TaxRate) t.IsRateOverridden = true;
            t.TaxRate = request.Body.TaxRate;
        }
        t.IsActive = request.Body.IsActive;
        t.UpdatedBy = _user.UserCode;
        t.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new TaxDto(t.Id, t.Seq, t.Code, t.Description, t.TaxRate, t.IsActive, t.CreatedOn,
            t.IsRateOverridden, t.LastSyncedRate);
    }
}
