using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using MedUnit = MediatR.Unit;   // disambiguate from the Unit entity imported above
using UnitEntity = MerinoOne.SupplierPortal.Domain.Entities.Mdm.Unit;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Masters.Mdm;

// Unit — company-scoped master CRUD. UnitType is the enum name (string) on the wire.

internal static class UnitProjection
{
    public static UnitType Parse(string? s) => Enum.TryParse<UnitType>(s, ignoreCase: true, out var t) ? t : UnitType.Quantity;

    public static async Task<UnitDto> ByIdAsync(IAppDbContext db, Guid id, CancellationToken ct)
        => await db.Units.Where(x => x.Id == id)
            .Select(x => new UnitDto(x.Id, x.Seq, x.Code, x.Description, x.UnitType.ToString(), x.IsoCode,
                x.DecimalPlaces, x.ConversionFactor, x.BaseUnitId, x.BaseUnit!.Code, x.IsActive, x.CreatedOn))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Unit", id);
}

public record GetUnitsQuery(bool? IsActive = null, string? Search = null) : IRequest<List<UnitDto>>;
public class GetUnitsQueryHandler(IAppDbContext db) : IRequestHandler<GetUnitsQuery, List<UnitDto>>
{
    public async Task<List<UnitDto>> Handle(GetUnitsQuery request, CancellationToken ct)
    {
        var q = db.Units.AsQueryable();
        if (request.IsActive.HasValue) q = q.Where(x => x.IsActive == request.IsActive.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Code)
            .Select(x => new UnitDto(x.Id, x.Seq, x.Code, x.Description, x.UnitType.ToString(), x.IsoCode,
                x.DecimalPlaces, x.ConversionFactor, x.BaseUnitId, x.BaseUnit!.Code, x.IsActive, x.CreatedOn))
            .ToListAsync(ct);
    }
}

public record GetUnitByIdQuery(Guid Id) : IRequest<UnitDto>;
public class GetUnitByIdQueryHandler(IAppDbContext db) : IRequestHandler<GetUnitByIdQuery, UnitDto>
{
    public Task<UnitDto> Handle(GetUnitByIdQuery request, CancellationToken ct) => UnitProjection.ByIdAsync(db, request.Id, ct);
}

public record CreateUnitCommand(CreateUnitRequest Body) : IRequest<UnitDto>;
public class CreateUnitCommandValidator : AbstractValidator<CreateUnitCommand>
{
    public CreateUnitCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.IsoCode).MaximumLength(10);
        RuleFor(x => x.Body.DecimalPlaces).InclusiveBetween(0, 6);
        RuleFor(x => x.Body.ConversionFactor).GreaterThan(0);
    }
}
public class CreateUnitCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<CreateUnitCommand, UnitDto>
{
    public async Task<UnitDto> Handle(CreateUnitCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        if (await db.Units.AnyAsync(x => x.Code == code, ct))
            throw new ConflictException($"Unit with code '{code}' already exists.");
        if (request.Body.BaseUnitId is Guid bid && !await db.Units.AnyAsync(u => u.Id == bid, ct))
            throw new NotFoundException("Unit", bid);

        var e = new UnitEntity
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            UnitType = UnitProjection.Parse(request.Body.UnitType),
            IsoCode = string.IsNullOrWhiteSpace(request.Body.IsoCode) ? null : request.Body.IsoCode.Trim(),
            DecimalPlaces = request.Body.DecimalPlaces,
            ConversionFactor = request.Body.ConversionFactor,
            BaseUnitId = request.Body.BaseUnitId,
            IsActive = request.Body.IsActive,
            CreatedBy = user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        db.Units.Add(e);
        await db.SaveChangesAsync(ct);
        return await UnitProjection.ByIdAsync(db, e.Id, ct);
    }
}

public record UpdateUnitCommand(Guid Id, UpdateUnitRequest Body) : IRequest<UnitDto>;
public class UpdateUnitCommandValidator : AbstractValidator<UpdateUnitCommand>
{
    public UpdateUnitCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.IsoCode).MaximumLength(10);
        RuleFor(x => x.Body.DecimalPlaces).InclusiveBetween(0, 6);
        RuleFor(x => x.Body.ConversionFactor).GreaterThan(0);
    }
}
public class UpdateUnitCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<UpdateUnitCommand, UnitDto>
{
    public async Task<UnitDto> Handle(UpdateUnitCommand request, CancellationToken ct)
    {
        var e = await db.Units.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Unit", request.Id);
        if (request.Body.BaseUnitId is Guid bid)
        {
            if (bid == e.Id)
                throw new ValidationException(new Dictionary<string, string[]> { ["baseUnitId"] = new[] { "A unit cannot be its own base unit." } });
            if (!await db.Units.AnyAsync(u => u.Id == bid, ct)) throw new NotFoundException("Unit", bid);
        }
        e.Description = request.Body.Description.Trim();
        e.UnitType = UnitProjection.Parse(request.Body.UnitType);
        e.IsoCode = string.IsNullOrWhiteSpace(request.Body.IsoCode) ? null : request.Body.IsoCode.Trim();
        e.DecimalPlaces = request.Body.DecimalPlaces;
        e.ConversionFactor = request.Body.ConversionFactor;
        e.BaseUnitId = request.Body.BaseUnitId;
        e.IsActive = request.Body.IsActive;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await UnitProjection.ByIdAsync(db, e.Id, ct);
    }
}

public record DeactivateUnitCommand(Guid Id) : IRequest<MedUnit>;
public class DeactivateUnitCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<DeactivateUnitCommand, MedUnit>
{
    public async Task<MedUnit> Handle(DeactivateUnitCommand request, CancellationToken ct)
    {
        var e = await db.Units.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Unit", request.Id);
        e.IsActive = false;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MedUnit.Value;
    }
}
