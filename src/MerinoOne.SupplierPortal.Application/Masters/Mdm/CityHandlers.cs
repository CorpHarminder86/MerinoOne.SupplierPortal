using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using Microsoft.EntityFrameworkCore;
using MedUnit = MediatR.Unit;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Masters.Mdm;

// City — tenant-scoped master CRUD. Country required; State optional (must belong to the country).

internal static class CityProjection
{
    public static async Task<CityDto> ByIdAsync(IAppDbContext db, Guid id, CancellationToken ct)
        => await db.Cities.Where(x => x.Id == id)
            .Select(x => new CityDto(x.Id, x.Seq, x.Code, x.Description, x.CountryId, x.Country!.Description, x.StateId, x.State!.Description, x.IsActive, x.CreatedOn))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("City", id);

    /// <summary>Validates Country exists and any supplied State exists and belongs to that Country.</summary>
    public static async Task ValidateParentsAsync(IAppDbContext db, Guid countryId, Guid? stateId, CancellationToken ct)
    {
        if (!await db.Countries.AnyAsync(c => c.Id == countryId, ct))
            throw new NotFoundException("Country", countryId);
        if (stateId is Guid sid)
        {
            var st = await db.States.Where(s => s.Id == sid).Select(s => new { s.CountryId }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("State", sid);
            if (st.CountryId != countryId)
                throw new ValidationException(new Dictionary<string, string[]> { ["stateId"] = new[] { "The selected state does not belong to the selected country." } });
        }
    }
}

public record GetCitiesQuery(bool? IsActive = null, string? Search = null, Guid? CountryId = null, Guid? StateId = null) : IRequest<List<CityDto>>;
public class GetCitiesQueryHandler(IAppDbContext db) : IRequestHandler<GetCitiesQuery, List<CityDto>>
{
    public async Task<List<CityDto>> Handle(GetCitiesQuery request, CancellationToken ct)
    {
        var q = db.Cities.AsQueryable();
        if (request.IsActive.HasValue) q = q.Where(x => x.IsActive == request.IsActive.Value);
        if (request.CountryId.HasValue) q = q.Where(x => x.CountryId == request.CountryId.Value);
        if (request.StateId.HasValue) q = q.Where(x => x.StateId == request.StateId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Description)
            .Select(x => new CityDto(x.Id, x.Seq, x.Code, x.Description, x.CountryId, x.Country!.Description, x.StateId, x.State!.Description, x.IsActive, x.CreatedOn))
            .ToListAsync(ct);
    }
}

public record GetCityByIdQuery(Guid Id) : IRequest<CityDto>;
public class GetCityByIdQueryHandler(IAppDbContext db) : IRequestHandler<GetCityByIdQuery, CityDto>
{
    public Task<CityDto> Handle(GetCityByIdQuery request, CancellationToken ct) => CityProjection.ByIdAsync(db, request.Id, ct);
}

public record CreateCityCommand(CreateCityRequest Body) : IRequest<CityDto>;
public class CreateCityCommandValidator : AbstractValidator<CreateCityCommand>
{
    public CreateCityCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.CountryId).NotEmpty();
    }
}
public class CreateCityCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<CreateCityCommand, CityDto>
{
    public async Task<CityDto> Handle(CreateCityCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        if (await db.Cities.AnyAsync(x => x.Code == code, ct))
            throw new ConflictException($"City with code '{code}' already exists.");
        await CityProjection.ValidateParentsAsync(db, request.Body.CountryId, request.Body.StateId, ct);

        var e = new City
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            CountryId = request.Body.CountryId,
            StateId = request.Body.StateId,
            IsActive = request.Body.IsActive,
            CreatedBy = user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        db.Cities.Add(e);
        await db.SaveChangesAsync(ct);
        return await CityProjection.ByIdAsync(db, e.Id, ct);
    }
}

public record UpdateCityCommand(Guid Id, UpdateCityRequest Body) : IRequest<CityDto>;
public class UpdateCityCommandValidator : AbstractValidator<UpdateCityCommand>
{
    public UpdateCityCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.CountryId).NotEmpty();
    }
}
public class UpdateCityCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<UpdateCityCommand, CityDto>
{
    public async Task<CityDto> Handle(UpdateCityCommand request, CancellationToken ct)
    {
        var e = await db.Cities.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("City", request.Id);
        await CityProjection.ValidateParentsAsync(db, request.Body.CountryId, request.Body.StateId, ct);

        e.Description = request.Body.Description.Trim();
        e.CountryId = request.Body.CountryId;
        e.StateId = request.Body.StateId;
        e.IsActive = request.Body.IsActive;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await CityProjection.ByIdAsync(db, e.Id, ct);
    }
}

public record DeactivateCityCommand(Guid Id) : IRequest<MedUnit>;
public class DeactivateCityCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<DeactivateCityCommand, MedUnit>
{
    public async Task<MedUnit> Handle(DeactivateCityCommand request, CancellationToken ct)
    {
        var e = await db.Cities.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("City", request.Id);
        e.IsActive = false;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MedUnit.Value;
    }
}
