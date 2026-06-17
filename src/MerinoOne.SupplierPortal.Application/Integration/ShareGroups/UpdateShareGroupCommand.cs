using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Integration.ShareGroups;

/// <summary>
/// Tenant-Admin: update a share group's display name + enabled flag. The endpoint, source and member set
/// are intentionally NOT editable here (changing them is a re-tag concern). Scoped to the caller's tenant
/// via <c>IgnoreQueryFilters()</c> + explicit <c>!IsDeleted</c> + tenant restriction, so a cross-tenant id
/// is simply not found.
/// </summary>
public record UpdateShareGroupCommand(Guid Id, UpdateShareGroupRequest Body) : IRequest<Unit>;

public class UpdateShareGroupCommandValidator : AbstractValidator<UpdateShareGroupCommand>
{
    public UpdateShareGroupCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(200);
    }
}

public class UpdateShareGroupCommandHandler : IRequestHandler<UpdateShareGroupCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateShareGroupCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(UpdateShareGroupCommand request, CancellationToken ct)
    {
        var tenantId = _user.TenantId;

        var group = await _db.CompanyShareGroups.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => !g.IsDeleted && g.TenantId == tenantId && g.Id == request.Id, ct)
            ?? throw new NotFoundException("CompanyShareGroup", request.Id);

        group.Name = request.Body.Name.Trim();
        group.IsEnabled = request.Body.IsEnabled;
        group.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        group.UpdatedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
