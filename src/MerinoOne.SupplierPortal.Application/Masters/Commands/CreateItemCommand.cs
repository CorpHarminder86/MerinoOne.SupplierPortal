using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record CreateItemCommand(CreateItemRequest Body) : IRequest<ItemDto>;

public class CreateItemCommandValidator : AbstractValidator<CreateItemCommand>
{
    public CreateItemCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Body.Uom).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.HsnCode).MaximumLength(20);
    }
}

public class CreateItemCommandHandler : IRequestHandler<CreateItemCommand, ItemDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public CreateItemCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<ItemDto> Handle(CreateItemCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        var exists = await _db.Items.AnyAsync(i => i.Code == code, ct);
        if (exists) throw new ConflictException($"Item with code '{code}' already exists.");

        var entity = new Item
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            Uom = request.Body.Uom.Trim(),
            HsnCode = string.IsNullOrWhiteSpace(request.Body.HsnCode) ? null : request.Body.HsnCode.Trim(),
            IsActive = request.Body.IsActive,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Items.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new ItemDto(entity.Id, entity.Seq, entity.Code, entity.Description, entity.Uom, entity.HsnCode, entity.IsActive, entity.CreatedOn);
    }
}
