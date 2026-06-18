using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Mdm;
using Microsoft.EntityFrameworkCore;
using MedUnit = MediatR.Unit;

namespace MerinoOne.SupplierPortal.Application.Masters.Mdm;

// Currency — tenant-scoped master CRUD.

public record GetCurrenciesQuery(bool? IsActive = null, string? Search = null) : IRequest<List<CurrencyDto>>;
public class GetCurrenciesQueryHandler(IAppDbContext db) : IRequestHandler<GetCurrenciesQuery, List<CurrencyDto>>
{
    public async Task<List<CurrencyDto>> Handle(GetCurrenciesQuery request, CancellationToken ct)
    {
        var q = db.Currencies.AsQueryable();
        if (request.IsActive.HasValue) q = q.Where(x => x.IsActive == request.IsActive.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.Code.Contains(t) || x.Description.Contains(t));
        }
        return await q.OrderBy(x => x.Code)
            .Select(x => new CurrencyDto(x.Id, x.Seq, x.Code, x.Description, x.IsoCode, x.Symbol, x.DecimalPlaces, x.IsActive, x.CreatedOn))
            .ToListAsync(ct);
    }
}

public record GetCurrencyByIdQuery(Guid Id) : IRequest<CurrencyDto>;
public class GetCurrencyByIdQueryHandler(IAppDbContext db) : IRequestHandler<GetCurrencyByIdQuery, CurrencyDto>
{
    public async Task<CurrencyDto> Handle(GetCurrencyByIdQuery request, CancellationToken ct)
        => await db.Currencies.Where(x => x.Id == request.Id)
            .Select(x => new CurrencyDto(x.Id, x.Seq, x.Code, x.Description, x.IsoCode, x.Symbol, x.DecimalPlaces, x.IsActive, x.CreatedOn))
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Currency", request.Id);
}

public record CreateCurrencyCommand(CreateCurrencyRequest Body) : IRequest<CurrencyDto>;
public class CreateCurrencyCommandValidator : AbstractValidator<CreateCurrencyCommand>
{
    public CreateCurrencyCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.IsoCode).MaximumLength(3);
        RuleFor(x => x.Body.Symbol).MaximumLength(10);
        RuleFor(x => x.Body.DecimalPlaces).InclusiveBetween(0, 6);
    }
}
public class CreateCurrencyCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<CreateCurrencyCommand, CurrencyDto>
{
    public async Task<CurrencyDto> Handle(CreateCurrencyCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        if (await db.Currencies.AnyAsync(x => x.Code == code, ct))
            throw new ConflictException($"Currency with code '{code}' already exists.");
        var e = new Currency
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            IsoCode = string.IsNullOrWhiteSpace(request.Body.IsoCode) ? null : request.Body.IsoCode.Trim(),
            Symbol = string.IsNullOrWhiteSpace(request.Body.Symbol) ? null : request.Body.Symbol.Trim(),
            DecimalPlaces = request.Body.DecimalPlaces,
            IsActive = request.Body.IsActive,
            CreatedBy = user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        db.Currencies.Add(e);
        await db.SaveChangesAsync(ct);
        return new CurrencyDto(e.Id, e.Seq, e.Code, e.Description, e.IsoCode, e.Symbol, e.DecimalPlaces, e.IsActive, e.CreatedOn);
    }
}

public record UpdateCurrencyCommand(Guid Id, UpdateCurrencyRequest Body) : IRequest<CurrencyDto>;
public class UpdateCurrencyCommandValidator : AbstractValidator<UpdateCurrencyCommand>
{
    public UpdateCurrencyCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.IsoCode).MaximumLength(3);
        RuleFor(x => x.Body.Symbol).MaximumLength(10);
        RuleFor(x => x.Body.DecimalPlaces).InclusiveBetween(0, 6);
    }
}
public class UpdateCurrencyCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<UpdateCurrencyCommand, CurrencyDto>
{
    public async Task<CurrencyDto> Handle(UpdateCurrencyCommand request, CancellationToken ct)
    {
        var e = await db.Currencies.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Currency", request.Id);
        e.Description = request.Body.Description.Trim();
        e.IsoCode = string.IsNullOrWhiteSpace(request.Body.IsoCode) ? null : request.Body.IsoCode.Trim();
        e.Symbol = string.IsNullOrWhiteSpace(request.Body.Symbol) ? null : request.Body.Symbol.Trim();
        e.DecimalPlaces = request.Body.DecimalPlaces;
        e.IsActive = request.Body.IsActive;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new CurrencyDto(e.Id, e.Seq, e.Code, e.Description, e.IsoCode, e.Symbol, e.DecimalPlaces, e.IsActive, e.CreatedOn);
    }
}

public record DeactivateCurrencyCommand(Guid Id) : IRequest<MedUnit>;
public class DeactivateCurrencyCommandHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<DeactivateCurrencyCommand, MedUnit>
{
    public async Task<MedUnit> Handle(DeactivateCurrencyCommand request, CancellationToken ct)
    {
        var e = await db.Currencies.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Currency", request.Id);
        e.IsActive = false;
        e.UpdatedBy = user.UserCode;
        e.UpdatedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return MedUnit.Value;
    }
}
