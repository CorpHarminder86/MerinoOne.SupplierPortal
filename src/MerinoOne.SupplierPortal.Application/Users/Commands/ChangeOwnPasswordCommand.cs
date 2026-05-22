using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Auth;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

/// <summary>
/// Self-service password change. Verifies the current password, hashes the new one,
/// and clears the <c>MustChangePassword</c> flag so the user can proceed past the
/// forced-change gate. Any authenticated user may call this for themselves.
/// </summary>
public record ChangeOwnPasswordCommand(ChangeOwnPasswordRequest Body) : IRequest<Unit>;

public class ChangeOwnPasswordCommandValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ChangeOwnPasswordCommandValidator()
    {
        RuleFor(x => x.Body.CurrentPassword)
            .NotEmpty().WithMessage("Current password is required.");
        RuleFor(x => x.Body.NewPassword)
            .NotEmpty().WithMessage("New password is required.")
            .MinimumLength(8).WithMessage("New password must be at least 8 chars.");
        RuleFor(x => x.Body)
            .Must(b => b.CurrentPassword != b.NewPassword)
            .WithName("newPassword")
            .WithMessage("New password must be different from the current password.");
    }
}

public class ChangeOwnPasswordCommandHandler : IRequestHandler<ChangeOwnPasswordCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _current;
    private readonly IPasswordHasher _hasher;

    public ChangeOwnPasswordCommandHandler(IAppDbContext db, ICurrentUser current, IPasswordHasher hasher)
    {
        _db = db;
        _current = current;
        _hasher = hasher;
    }

    public async Task<Unit> Handle(ChangeOwnPasswordCommand request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_current.UserCode))
            throw new UnauthorizedAccessException("Not authenticated.");

        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.UserCode == _current.UserCode, ct)
            ?? throw new NotFoundException("User", _current.UserCode);

        if (!_hasher.Verify(request.Body.CurrentPassword, user.PasswordHash))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["currentPassword"] = new[] { "Current password is incorrect." }
            });
        }

        var now = DateTime.UtcNow;
        user.PasswordHash = _hasher.Hash(request.Body.NewPassword);
        user.MustChangePassword = false;
        user.UpdatedBy = _current.UserCode;
        user.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
