using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using Microsoft.EntityFrameworkCore;
using MedUnit = MediatR.Unit;

namespace MerinoOne.SupplierPortal.Application.Masters.Mdm;

// State — tenant-scoped master CRUD. Belongs to a Country (required).

internal static class StateProjection
{
    public static async Task<StateDto> ByIdAsync(IAppDbContext db, Guid id, CancellationToken ct)
        => await db.States.Where(x => x.Id == id)
            .Select(x => new StateDto(x.Id, x.Seq, x.Code, x.Description, x.CountryId, x.Country!.Code, x.Country!.Description, x.IsoCode, x.IsActive, x.CreatedOn))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("State", id);
}

public record GetStatesQuery(bool? IsActive = null, string? Search = null, Guid? CountryId = null) : IRequest<List<StateDto>>;
public class GetStatesQueryHandler(IAppDbContext db) : IRequestHandler<GetStatesQuery, List<StateDto>>
{
    public async Task<List<StateDto>> Handle(GetStatesQuery request, CancellationToken ct)
    {
        var q = db.States.AsQueryable();
        if (request.IsActive.HasValue) q = q.Where(x => x.IsActive == request.IsActive.Value);
        if (request.CountryId.HasValue) q = q.Where(x => x.CountryId == request.CountryId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Description)
            .Select(x => new StateDto(x.Id, x.Seq, x.Code, x.Description, x.CountryId, x.Country!.Code, x.Country!.Description, x.IsoCode, x.IsActive, x.CreatedOn))
            .ToListAsync(ct);
    }
}

public record GetStateByIdQuery(Guid Id) : IRequest<StateDto>;
public class GetStateByIdQueryHandler(IAppDbContext db) : IRequestHandler<GetStateByIdQuery, StateDto>
{
    public Task<StateDto> Handle(GetStateByIdQuery request, CancellationToken ct) => StateProjection.ByIdAsync(db, request.Id, ct);
}

public record CreateStateCommand(CreateStateRequest Body) : IRequest<StateDto>;
public class CreateStateCommandValidator : AbstractValidator<CreateStateCommand>
{
    public CreateStateCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.CountryId).NotEmpty();
        RuleFor(x => x.Body.IsoCode).MaximumLength(10);
    }
}
public class CreateStateCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<CreateStateCommand, StateDto>
{
    public async Task<StateDto> Handle(CreateStateCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        if (await db.States.AnyAsync(x => x.Code == code, ct))
            throw new ConflictException($"State with code '{code}' already exists.");
        if (!await db.Countries.AnyAsync(c => c.Id == request.Body.CountryId, ct))
            throw new NotFoundException("Country", request.Body.CountryId);

        var e = new State
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            CountryId = request.Body.CountryId,
            IsoCode = string.IsNullOrWhiteSpace(request.Body.IsoCode) ? null : request.Body.IsoCode.Trim(),
            IsActive = request.Body.IsActive,
            CreatedBy = user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        db.States.Add(e);
        await db.SaveChangesAsync(ct);
        return await StateProjection.ByIdAsync(db, e.Id, ct);
    }
}

public record UpdateStateCommand(Guid Id, UpdateStateRequest Body) : IRequest<StateDto>;
public class UpdateStateCommandValidator : AbstractValidator<UpdateStateCommand>
{
    public UpdateStateCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.CountryId).NotEmpty();
        RuleFor(x => x.Body.IsoCode).MaximumLength(10);
    }
}
public class UpdateStateCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<UpdateStateCommand, StateDto>
{
    public async Task<StateDto> Handle(UpdateStateCommand request, CancellationToken ct)
    {
        var e = await db.States.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("State", request.Id);
        if (!await db.Countries.AnyAsync(c => c.Id == request.Body.CountryId, ct))
            throw new NotFoundException("Country", request.Body.CountryId);

        e.Description = request.Body.Description.Trim();
        e.CountryId = request.Body.CountryId;
        e.IsoCode = string.IsNullOrWhiteSpace(request.Body.IsoCode) ? null : request.Body.IsoCode.Trim();
        e.IsActive = request.Body.IsActive;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await StateProjection.ByIdAsync(db, e.Id, ct);
    }
}

public record DeactivateStateCommand(Guid Id) : IRequest<MedUnit>;
public class DeactivateStateCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<DeactivateStateCommand, MedUnit>
{
    public async Task<MedUnit> Handle(DeactivateStateCommand request, CancellationToken ct)
    {
        var e = await db.States.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("State", request.Id);
        e.IsActive = false;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MedUnit.Value;
    }
}
