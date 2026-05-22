using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record CreatePaymentTermCommand(CreatePaymentTermRequest Body) : IRequest<PaymentTermDto>;

public class CreatePaymentTermCommandValidator : AbstractValidator<CreatePaymentTermCommand>
{
    public CreatePaymentTermCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.NetDays).GreaterThanOrEqualTo(0).LessThanOrEqualTo(365);
    }
}

public class CreatePaymentTermCommandHandler : IRequestHandler<CreatePaymentTermCommand, PaymentTermDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public CreatePaymentTermCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<PaymentTermDto> Handle(CreatePaymentTermCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        var exists = await _db.PaymentTerms.AnyAsync(p => p.Code == code, ct);
        if (exists) throw new ConflictException($"PaymentTerm with code '{code}' already exists.");

        var entity = new PaymentTerm
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            NetDays = request.Body.NetDays,
            IsActive = request.Body.IsActive,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.PaymentTerms.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new PaymentTermDto(entity.Id, entity.Seq, entity.Code, entity.Description, entity.NetDays, entity.IsActive, entity.CreatedOn);
    }
}
