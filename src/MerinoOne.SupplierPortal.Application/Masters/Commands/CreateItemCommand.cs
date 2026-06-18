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

        string? groupCode = null, unitCode = null;
        if (request.Body.ItemGroupId is Guid gid)
            groupCode = await _db.ItemGroups.Where(g => g.Id == gid).Select(g => g.Code).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("ItemGroup", gid);
        if (request.Body.UnitId is Guid uid)
            unitCode = await _db.Units.Where(u => u.Id == uid).Select(u => u.Code).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Unit", uid);

        var entity = new Item
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            HsnCode = string.IsNullOrWhiteSpace(request.Body.HsnCode) ? null : request.Body.HsnCode.Trim(),
            ItemGroupId = request.Body.ItemGroupId,
            UnitId = request.Body.UnitId,
            IsActive = request.Body.IsActive,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Items.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new ItemDto(entity.Id, entity.Seq, entity.Code, entity.Description, entity.HsnCode,
            entity.ItemGroupId, groupCode, entity.UnitId, unitCode, entity.IsActive, entity.CreatedOn);
    }
}
