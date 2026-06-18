using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Mdm;

// ItemGroup — company-scoped master CRUD (mirrors the PaymentTerm slice).

public record GetItemGroupsQuery(bool? IsActive = null, string? Search = null) : IRequest<List<ItemGroupDto>>;
public class GetItemGroupsQueryHandler(IAppDbContext db) : IRequestHandler<GetItemGroupsQuery, List<ItemGroupDto>>
{
    public async Task<List<ItemGroupDto>> Handle(GetItemGroupsQuery request, CancellationToken ct)
    {
        var q = db.ItemGroups.AsQueryable();
        if (request.IsActive.HasValue) q = q.Where(x => x.IsActive == request.IsActive.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Code)
            .Select(x => new ItemGroupDto(x.Id, x.Seq, x.Code, x.Description, x.IsActive, x.CreatedOn))
            .ToListAsync(ct);
    }
}

public record GetItemGroupByIdQuery(Guid Id) : IRequest<ItemGroupDto>;
public class GetItemGroupByIdQueryHandler(IAppDbContext db) : IRequestHandler<GetItemGroupByIdQuery, ItemGroupDto>
{
    public async Task<ItemGroupDto> Handle(GetItemGroupByIdQuery request, CancellationToken ct)
        => await db.ItemGroups.Where(x => x.Id == request.Id)
            .Select(x => new ItemGroupDto(x.Id, x.Seq, x.Code, x.Description, x.IsActive, x.CreatedOn))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("ItemGroup", request.Id);
}

public record CreateItemGroupCommand(CreateItemGroupRequest Body) : IRequest<ItemGroupDto>;
public class CreateItemGroupCommandValidator : AbstractValidator<CreateItemGroupCommand>
{
    public CreateItemGroupCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(200);
    }
}
public class CreateItemGroupCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<CreateItemGroupCommand, ItemGroupDto>
{
    public async Task<ItemGroupDto> Handle(CreateItemGroupCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        if (await db.ItemGroups.AnyAsync(x => x.Code == code, ct))
            throw new ConflictException($"Item group with code '{code}' already exists.");
        var e = new ItemGroup
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            IsActive = request.Body.IsActive,
            CreatedBy = user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        db.ItemGroups.Add(e);
        await db.SaveChangesAsync(ct);
        return new ItemGroupDto(e.Id, e.Seq, e.Code, e.Description, e.IsActive, e.CreatedOn);
    }
}

public record UpdateItemGroupCommand(Guid Id, UpdateItemGroupRequest Body) : IRequest<ItemGroupDto>;
public class UpdateItemGroupCommandValidator : AbstractValidator<UpdateItemGroupCommand>
{
    public UpdateItemGroupCommandValidator() => RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(200);
}
public class UpdateItemGroupCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<UpdateItemGroupCommand, ItemGroupDto>
{
    public async Task<ItemGroupDto> Handle(UpdateItemGroupCommand request, CancellationToken ct)
    {
        var e = await db.ItemGroups.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("ItemGroup", request.Id);
        e.Description = request.Body.Description.Trim();
        e.IsActive = request.Body.IsActive;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new ItemGroupDto(e.Id, e.Seq, e.Code, e.Description, e.IsActive, e.CreatedOn);
    }
}

public record DeactivateItemGroupCommand(Guid Id) : IRequest<Unit>;
public class DeactivateItemGroupCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<DeactivateItemGroupCommand, Unit>
{
    public async Task<Unit> Handle(DeactivateItemGroupCommand request, CancellationToken ct)
    {
        var e = await db.ItemGroups.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("ItemGroup", request.Id);
        e.IsActive = false;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
