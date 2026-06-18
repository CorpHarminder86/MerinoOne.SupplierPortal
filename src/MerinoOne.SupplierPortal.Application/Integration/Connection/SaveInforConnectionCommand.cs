using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Integration.Connection;

/// <summary>
/// Upserts the current tenant's Infor connection config (one row per tenant). Secrets are encrypted via
/// <see cref="ISettingProtector"/>; an incoming secret equal to <see cref="InforConnectionSecret.Mask"/>
/// leaves the stored ciphertext untouched (the operator did not retype it).
/// </summary>
public record SaveInforConnectionCommand(SaveInforConnectionRequest Body) : IRequest<Unit>;

public class SaveInforConnectionCommandValidator : AbstractValidator<SaveInforConnectionCommand>
{
    public SaveInforConnectionCommandValidator()
    {
        RuleFor(x => x.Body.AccessTokenUrl).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Body.ClientId).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Body.ClientSecret).NotNull().MaximumLength(4000);
        RuleFor(x => x.Body.Username).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Body.Password).NotNull().MaximumLength(4000);
        RuleFor(x => x.Body.ApiBaseUrl).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Body.IonC4wsBaseUrl).MaximumLength(500);
        RuleFor(x => x.Body.Company).MaximumLength(200);
    }
}

public class SaveInforConnectionCommandHandler : IRequestHandler<SaveInforConnectionCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISettingProtector _protector;

    public SaveInforConnectionCommandHandler(IAppDbContext db, ICurrentUser user, ISettingProtector protector)
    {
        _db = db;
        _user = user;
        _protector = protector;
    }

    public async Task<Unit> Handle(SaveInforConnectionCommand request, CancellationToken ct)
    {
        var tenantId = _user.TenantId
            ?? throw new ValidationException(new Dictionary<string, string[]>
            {
                ["TenantId"] = new[] { "Infor connection config is tenant-scoped and cannot be saved without a tenant context." }
            });

        var body = request.Body;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
        var now = DateTime.UtcNow;

        var row = await _db.InforConnectionSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        if (row is null)
        {
            row = new InforConnectionSetting
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                IsActive = true,
                CreatedBy = actor,
                CreatedOn = now,
            };
            _db.InforConnectionSettings.Add(row);
        }
        else
        {
            row.UpdatedBy = actor;
            row.UpdatedOn = now;
        }

        row.AccessTokenUrl = body.AccessTokenUrl.Trim();
        row.ClientId = body.ClientId.Trim();
        row.Username = body.Username.Trim();
        row.ApiBaseUrl = body.ApiBaseUrl.Trim();
        row.IonC4wsBaseUrl = string.IsNullOrWhiteSpace(body.IonC4wsBaseUrl) ? null : body.IonC4wsBaseUrl.Trim();
        row.Company = string.IsNullOrWhiteSpace(body.Company) ? null : body.Company.Trim();

        row.ClientSecret = ResolveSecret(body.ClientSecret, row.ClientSecret);
        row.Password = ResolveSecret(body.Password, row.Password);

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }

    /// <summary>Mask sentinel → keep the existing ciphertext. Blank → clear. Anything else → encrypt.</summary>
    private string ResolveSecret(string incoming, string existing)
    {
        if (string.Equals(incoming, InforConnectionSecret.Mask, StringComparison.Ordinal))
            return existing ?? string.Empty;
        return string.IsNullOrEmpty(incoming) ? string.Empty : _protector.Protect(incoming);
    }
}
