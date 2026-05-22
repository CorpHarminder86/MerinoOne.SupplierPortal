using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Roles.Commands;

public record UpdateRoleCommand(Guid Id, UpdateRoleRequest Body) : IRequest<Unit>;

public class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleCommandValidator()
    {
        RuleFor(x => x.Body.Name)
            .NotEmpty().WithMessage("Role name is required.")
            .MaximumLength(50);
    }
}

public class UpdateRoleCommandHandler : IRequestHandler<UpdateRoleCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateRoleCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(UpdateRoleCommand request, CancellationToken ct)
    {
        var role = await _db.Roles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == request.Id, ct)
            ?? throw new NotFoundException("Role", request.Id);

        if (!string.Equals(role.Name, request.Body.Name, StringComparison.Ordinal))
        {
            var owned = await _db.Roles.IgnoreQueryFilters()
                .AnyAsync(r => r.Id != role.Id && r.Name == request.Body.Name, ct);
            if (owned) throw new ConflictException($"Role '{request.Body.Name}' already exists.");
        }

        role.Name = request.Body.Name;
        role.UpdatedOn = DateTime.UtcNow;
        role.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
