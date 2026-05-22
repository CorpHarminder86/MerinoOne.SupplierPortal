using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record SetPasswordCommand(Guid UserId, SetPasswordRequest Body) : IRequest<Unit>;

public class SetPasswordCommandValidator : AbstractValidator<SetPasswordCommand>
{
    public SetPasswordCommandValidator()
    {
        RuleFor(x => x.Body.NewPassword)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 chars.");
    }
}

public class SetPasswordCommandHandler : IRequestHandler<SetPasswordCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IPasswordHasher _hasher;

    public SetPasswordCommandHandler(IAppDbContext db, ICurrentUser user, IPasswordHasher hasher)
    {
        _db = db;
        _user = user;
        _hasher = hasher;
    }

    public async Task<Unit> Handle(SetPasswordCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
            ?? throw new NotFoundException("User", request.UserId);

        user.PasswordHash = _hasher.Hash(request.Body.NewPassword);
        user.UpdatedOn = DateTime.UtcNow;
        user.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
