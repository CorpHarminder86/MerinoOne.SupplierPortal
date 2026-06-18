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

// PostalCode — tenant-scoped master CRUD. Country required; State + City optional and chain-validated.

internal static class PostalCodeProjection
{
    public static async Task<PostalCodeDto> ByIdAsync(IAppDbContext db, Guid id, CancellationToken ct)
        => await db.PostalCodes.Where(x => x.Id == id)
            .Select(x => new PostalCodeDto(x.Id, x.Seq, x.Code, x.Area, x.CountryId, x.Country!.Description,
                x.StateId, x.State!.Description, x.CityId, x.City!.Description, x.IsActive, x.CreatedOn))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("PostalCode", id);

    public static async Task ValidateParentsAsync(IAppDbContext db, Guid countryId, Guid? stateId, Guid? cityId, CancellationToken ct)
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
        if (cityId is Guid cid)
        {
            var city = await db.Cities.Where(c => c.Id == cid).Select(c => new { c.CountryId, c.StateId }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("City", cid);
            if (city.CountryId != countryId)
                throw new ValidationException(new Dictionary<string, string[]> { ["cityId"] = new[] { "The selected city does not belong to the selected country." } });
            if (stateId is Guid s2 && city.StateId.HasValue && city.StateId != s2)
                throw new ValidationException(new Dictionary<string, string[]> { ["cityId"] = new[] { "The selected city does not belong to the selected state." } });
        }
    }
}

public record GetPostalCodesQuery(bool? IsActive = null, string? Search = null, Guid? CountryId = null, Guid? StateId = null, Guid? CityId = null) : IRequest<List<PostalCodeDto>>;
public class GetPostalCodesQueryHandler(IAppDbContext db) : IRequestHandler<GetPostalCodesQuery, List<PostalCodeDto>>
{
    public async Task<List<PostalCodeDto>> Handle(GetPostalCodesQuery request, CancellationToken ct)
    {
        var q = db.PostalCodes.AsQueryable();
        if (request.IsActive.HasValue) q = q.Where(x => x.IsActive == request.IsActive.Value);
        if (request.CountryId.HasValue) q = q.Where(x => x.CountryId == request.CountryId.Value);
        if (request.StateId.HasValue) q = q.Where(x => x.StateId == request.StateId.Value);
        if (request.CityId.HasValue) q = q.Where(x => x.CityId == request.CityId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || (x.Area != null && x.Area.Contains(t)));
        }
        return await q.OrderBy(x => x.Code)
            .Select(x => new PostalCodeDto(x.Id, x.Seq, x.Code, x.Area, x.CountryId, x.Country!.Description,
                x.StateId, x.State!.Description, x.CityId, x.City!.Description, x.IsActive, x.CreatedOn))
            .ToListAsync(ct);
    }
}

public record GetPostalCodeByIdQuery(Guid Id) : IRequest<PostalCodeDto>;
public class GetPostalCodeByIdQueryHandler(IAppDbContext db) : IRequestHandler<GetPostalCodeByIdQuery, PostalCodeDto>
{
    public Task<PostalCodeDto> Handle(GetPostalCodeByIdQuery request, CancellationToken ct) => PostalCodeProjection.ByIdAsync(db, request.Id, ct);
}

public record CreatePostalCodeCommand(CreatePostalCodeRequest Body) : IRequest<PostalCodeDto>;
public class CreatePostalCodeCommandValidator : AbstractValidator<CreatePostalCodeCommand>
{
    public CreatePostalCodeCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Area).MaximumLength(150);
        RuleFor(x => x.Body.CountryId).NotEmpty();
    }
}
public class CreatePostalCodeCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<CreatePostalCodeCommand, PostalCodeDto>
{
    public async Task<PostalCodeDto> Handle(CreatePostalCodeCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        if (await db.PostalCodes.AnyAsync(x => x.Code == code, ct))
            throw new ConflictException($"Postal code '{code}' already exists.");
        await PostalCodeProjection.ValidateParentsAsync(db, request.Body.CountryId, request.Body.StateId, request.Body.CityId, ct);

        var e = new PostalCode
        {
            Id = Guid.NewGuid(),
            Code = code,
            Area = string.IsNullOrWhiteSpace(request.Body.Area) ? null : request.Body.Area.Trim(),
            CountryId = request.Body.CountryId,
            StateId = request.Body.StateId,
            CityId = request.Body.CityId,
            IsActive = request.Body.IsActive,
            CreatedBy = user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        db.PostalCodes.Add(e);
        await db.SaveChangesAsync(ct);
        return await PostalCodeProjection.ByIdAsync(db, e.Id, ct);
    }
}

public record UpdatePostalCodeCommand(Guid Id, UpdatePostalCodeRequest Body) : IRequest<PostalCodeDto>;
public class UpdatePostalCodeCommandValidator : AbstractValidator<UpdatePostalCodeCommand>
{
    public UpdatePostalCodeCommandValidator()
    {
        RuleFor(x => x.Body.Area).MaximumLength(150);
        RuleFor(x => x.Body.CountryId).NotEmpty();
    }
}
public class UpdatePostalCodeCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<UpdatePostalCodeCommand, PostalCodeDto>
{
    public async Task<PostalCodeDto> Handle(UpdatePostalCodeCommand request, CancellationToken ct)
    {
        var e = await db.PostalCodes.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("PostalCode", request.Id);
        await PostalCodeProjection.ValidateParentsAsync(db, request.Body.CountryId, request.Body.StateId, request.Body.CityId, ct);

        e.Area = string.IsNullOrWhiteSpace(request.Body.Area) ? null : request.Body.Area.Trim();
        e.CountryId = request.Body.CountryId;
        e.StateId = request.Body.StateId;
        e.CityId = request.Body.CityId;
        e.IsActive = request.Body.IsActive;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await PostalCodeProjection.ByIdAsync(db, e.Id, ct);
    }
}

public record DeactivatePostalCodeCommand(Guid Id) : IRequest<MedUnit>;
public class DeactivatePostalCodeCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<DeactivatePostalCodeCommand, MedUnit>
{
    public async Task<MedUnit> Handle(DeactivatePostalCodeCommand request, CancellationToken ct)
    {
        var e = await db.PostalCodes.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("PostalCode", request.Id);
        e.IsActive = false;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MedUnit.Value;
    }
}
