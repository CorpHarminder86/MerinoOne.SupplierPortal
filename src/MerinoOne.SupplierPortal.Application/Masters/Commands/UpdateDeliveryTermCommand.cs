using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record UpdateDeliveryTermCommand(Guid Id, UpdateDeliveryTermRequest Body) : IRequest<MasterItemDto>;

public class UpdateDeliveryTermCommandValidator : AbstractValidator<UpdateDeliveryTermCommand>
{
    public UpdateDeliveryTermCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(200);
    }
}

public class UpdateDeliveryTermCommandHandler : IRequestHandler<UpdateDeliveryTermCommand, MasterItemDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdateDeliveryTermCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<MasterItemDto> Handle(UpdateDeliveryTermCommand request, CancellationToken ct)
    {
        var d = await _db.DeliveryTerms.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("DeliveryTerm", request.Id);

        d.Description = request.Body.Description.Trim();
        d.IsActive = request.Body.IsActive;
        d.UpdatedBy = _user.UserCode;
        d.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new MasterItemDto(d.Id, d.Seq, d.Code, d.Description, d.IsActive, d.CreatedOn);
    }
}
