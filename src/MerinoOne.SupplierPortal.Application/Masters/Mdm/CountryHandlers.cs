using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using Microsoft.EntityFrameworkCore;
using MedUnit = MediatR.Unit;

namespace MerinoOne.SupplierPortal.Application.Masters.Mdm;

// Country — tenant-scoped master CRUD. Optional Currency FK (CurrencyCode shown on the DTO).

internal static class CountryProjection
{
    public static async Task<CountryDto> ByIdAsync(IAppDbContext db, Guid id, CancellationToken ct)
        => await db.Countries.Where(x => x.Id == id)
            .Select(x => new CountryDto(x.Id, x.Seq, x.Code, x.Description, x.IsoCode2, x.IsoCode3, x.TelephoneCode,
                x.CurrencyId, x.Currency!.Code, x.IsActive, x.CreatedOn))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Country", id);
}

public record GetCountriesQuery(bool? IsActive = null, string? Search = null) : IRequest<List<CountryDto>>;
public class GetCountriesQueryHandler(IAppDbContext db) : IRequestHandler<GetCountriesQuery, List<CountryDto>>
{
    public async Task<List<CountryDto>> Handle(GetCountriesQuery request, CancellationToken ct)
    {
        var q = db.Countries.AsQueryable();
        if (request.IsActive.HasValue) q = q.Where(x => x.IsActive == request.IsActive.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Description)
            .Select(x => new CountryDto(x.Id, x.Seq, x.Code, x.Description, x.IsoCode2, x.IsoCode3, x.TelephoneCode,
                x.CurrencyId, x.Currency!.Code, x.IsActive, x.CreatedOn))
            .ToListAsync(ct);
    }
}

public record GetCountryByIdQuery(Guid Id) : IRequest<CountryDto>;
public class GetCountryByIdQueryHandler(IAppDbContext db) : IRequestHandler<GetCountryByIdQuery, CountryDto>
{
    public Task<CountryDto> Handle(GetCountryByIdQuery request, CancellationToken ct) => CountryProjection.ByIdAsync(db, request.Id, ct);
}

public record CreateCountryCommand(CreateCountryRequest Body) : IRequest<CountryDto>;
public class CreateCountryCommandValidator : AbstractValidator<CreateCountryCommand>
{
    public CreateCountryCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.IsoCode2).MaximumLength(2);
        RuleFor(x => x.Body.IsoCode3).MaximumLength(3);
        RuleFor(x => x.Body.TelephoneCode).MaximumLength(10);
    }
}
public class CreateCountryCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<CreateCountryCommand, CountryDto>
{
    public async Task<CountryDto> Handle(CreateCountryCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        if (await db.Countries.AnyAsync(x => x.Code == code, ct))
            throw new ConflictException($"Country with code '{code}' already exists.");
        if (request.Body.CurrencyId is Guid cid && !await db.Currencies.AnyAsync(c => c.Id == cid, ct))
            throw new NotFoundException("Currency", cid);

        var e = new Country
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            IsoCode2 = Norm(request.Body.IsoCode2),
            IsoCode3 = Norm(request.Body.IsoCode3),
            TelephoneCode = Norm(request.Body.TelephoneCode),
            CurrencyId = request.Body.CurrencyId,
            IsActive = request.Body.IsActive,
            CreatedBy = user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        db.Countries.Add(e);
        await db.SaveChangesAsync(ct);
        return await CountryProjection.ByIdAsync(db, e.Id, ct);
    }
    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public record UpdateCountryCommand(Guid Id, UpdateCountryRequest Body) : IRequest<CountryDto>;
public class UpdateCountryCommandValidator : AbstractValidator<UpdateCountryCommand>
{
    public UpdateCountryCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.IsoCode2).MaximumLength(2);
        RuleFor(x => x.Body.IsoCode3).MaximumLength(3);
        RuleFor(x => x.Body.TelephoneCode).MaximumLength(10);
    }
}
public class UpdateCountryCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<UpdateCountryCommand, CountryDto>
{
    public async Task<CountryDto> Handle(UpdateCountryCommand request, CancellationToken ct)
    {
        var e = await db.Countries.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Country", request.Id);
        if (request.Body.CurrencyId is Guid cid && !await db.Currencies.AnyAsync(c => c.Id == cid, ct))
            throw new NotFoundException("Currency", cid);

        e.Description = request.Body.Description.Trim();
        e.IsoCode2 = Norm(request.Body.IsoCode2);
        e.IsoCode3 = Norm(request.Body.IsoCode3);
        e.TelephoneCode = Norm(request.Body.TelephoneCode);
        e.CurrencyId = request.Body.CurrencyId;
        e.IsActive = request.Body.IsActive;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await CountryProjection.ByIdAsync(db, e.Id, ct);
    }
    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public record DeactivateCountryCommand(Guid Id) : IRequest<MedUnit>;
public class DeactivateCountryCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<DeactivateCountryCommand, MedUnit>
{
    public async Task<MedUnit> Handle(DeactivateCountryCommand request, CancellationToken ct)
    {
        var e = await db.Countries.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Country", request.Id);
        e.IsActive = false;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MedUnit.Value;
    }
}
