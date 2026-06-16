using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Integration.Commands;

/// <summary>
/// Tenant-Admin: update an Infor endpoint's config (URL, BOD name, enabled flag). The always-on tenant
/// filter scopes the lookup to the caller's tenant, so a cross-tenant id simply won't be found.
/// </summary>
public record UpdateInforEndpointCommand(Guid Id, UpdateInforEndpointRequest Body) : IRequest<Unit>;

public class UpdateInforEndpointCommandValidator : AbstractValidator<UpdateInforEndpointCommand>
{
    public UpdateInforEndpointCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.InforEndpointUrl).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Body.BodName).MaximumLength(100);
    }
}

public class UpdateInforEndpointCommandHandler : IRequestHandler<UpdateInforEndpointCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateInforEndpointCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(UpdateInforEndpointCommand request, CancellationToken ct)
    {
        var map = await _db.InforEndpointMaps.FirstOrDefaultAsync(m => m.Id == request.Id, ct)
                  ?? throw new NotFoundException("InforEndpointMap", request.Id);

        map.InforEndpointUrl = request.Body.InforEndpointUrl.Trim();
        map.BodName = string.IsNullOrWhiteSpace(request.Body.BodName) ? null : request.Body.BodName.Trim();
        map.IsEnabled = request.Body.IsEnabled;
        map.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        map.UpdatedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

/// <summary>
/// Tenant-Admin: flip an endpoint's enabled flag (inbound kill-switch). When disabled, the inbound upsert
/// path rejects pushes with 403 (TenantCompany §4 step 4).
/// </summary>
public record ToggleInforEndpointCommand(Guid Id) : IRequest<bool>;

public class ToggleInforEndpointCommandHandler : IRequestHandler<ToggleInforEndpointCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public ToggleInforEndpointCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<bool> Handle(ToggleInforEndpointCommand request, CancellationToken ct)
    {
        var map = await _db.InforEndpointMaps.FirstOrDefaultAsync(m => m.Id == request.Id, ct)
                  ?? throw new NotFoundException("InforEndpointMap", request.Id);

        map.IsEnabled = !map.IsEnabled;
        map.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        map.UpdatedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return map.IsEnabled;
    }
}
