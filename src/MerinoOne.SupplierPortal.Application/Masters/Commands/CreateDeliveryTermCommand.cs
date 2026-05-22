using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record CreateDeliveryTermCommand(CreateDeliveryTermRequest Body) : IRequest<MasterItemDto>;

public class CreateDeliveryTermCommandValidator : AbstractValidator<CreateDeliveryTermCommand>
{
    public CreateDeliveryTermCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(200);
    }
}

public class CreateDeliveryTermCommandHandler : IRequestHandler<CreateDeliveryTermCommand, MasterItemDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public CreateDeliveryTermCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<MasterItemDto> Handle(CreateDeliveryTermCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        var exists = await _db.DeliveryTerms.AnyAsync(d => d.Code == code, ct);
        if (exists) throw new ConflictException($"DeliveryTerm with code '{code}' already exists.");

        var entity = new DeliveryTerm
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            IsActive = request.Body.IsActive,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.DeliveryTerms.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new MasterItemDto(entity.Id, entity.Seq, entity.Code, entity.Description, entity.IsActive, entity.CreatedOn);
    }
}
