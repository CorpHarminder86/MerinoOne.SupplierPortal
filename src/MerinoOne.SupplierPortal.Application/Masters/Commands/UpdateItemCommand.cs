using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record UpdateItemCommand(Guid Id, UpdateItemRequest Body) : IRequest<ItemDto>;

public class UpdateItemCommandValidator : AbstractValidator<UpdateItemCommand>
{
    public UpdateItemCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Body.Uom).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.HsnCode).MaximumLength(20);
    }
}

public class UpdateItemCommandHandler : IRequestHandler<UpdateItemCommand, ItemDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdateItemCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<ItemDto> Handle(UpdateItemCommand request, CancellationToken ct)
    {
        var i = await _db.Items.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Item", request.Id);

        i.Description = request.Body.Description.Trim();
        i.Uom = request.Body.Uom.Trim();
        i.HsnCode = string.IsNullOrWhiteSpace(request.Body.HsnCode) ? null : request.Body.HsnCode.Trim();
        i.IsActive = request.Body.IsActive;
        i.UpdatedBy = _user.UserCode;
        i.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new ItemDto(i.Id, i.Seq, i.Code, i.Description, i.Uom, i.HsnCode, i.IsActive, i.CreatedOn);
    }
}
