using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record UpdateUserCommand(Guid Id, UpdateUserRequest Body) : IRequest<Unit>;

public class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserCommandValidator()
    {
        RuleFor(x => x.Body.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}

public class UpdateUserCommandHandler : IRequestHandler<UpdateUserCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateUserCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(UpdateUserCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.Id, ct)
            ?? throw new NotFoundException("User", request.Id);

        if (!string.Equals(user.Email, request.Body.Email, StringComparison.OrdinalIgnoreCase))
        {
            var emailOwned = await _db.AppUsers.IgnoreQueryFilters()
                .AnyAsync(u => u.Id != user.Id && u.Email == request.Body.Email, ct);
            if (emailOwned)
                throw new ConflictException($"Email '{request.Body.Email}' is already in use.");
        }

        user.FullName = request.Body.FullName;
        user.Email = request.Body.Email;
        user.IsInternal = request.Body.IsInternal;
        user.IsMfaEnabled = request.Body.IsMfaEnabled;
        user.UpdatedOn = DateTime.UtcNow;
        user.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
