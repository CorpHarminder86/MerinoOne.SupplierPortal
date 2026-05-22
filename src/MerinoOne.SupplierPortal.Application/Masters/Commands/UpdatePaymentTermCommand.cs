using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record UpdatePaymentTermCommand(Guid Id, UpdatePaymentTermRequest Body) : IRequest<PaymentTermDto>;

public class UpdatePaymentTermCommandValidator : AbstractValidator<UpdatePaymentTermCommand>
{
    public UpdatePaymentTermCommandValidator()
    {
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.NetDays).GreaterThanOrEqualTo(0).LessThanOrEqualTo(365);
    }
}

public class UpdatePaymentTermCommandHandler : IRequestHandler<UpdatePaymentTermCommand, PaymentTermDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdatePaymentTermCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<PaymentTermDto> Handle(UpdatePaymentTermCommand request, CancellationToken ct)
    {
        var p = await _db.PaymentTerms.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("PaymentTerm", request.Id);

        p.Description = request.Body.Description.Trim();
        p.NetDays = request.Body.NetDays;
        p.IsActive = request.Body.IsActive;
        p.UpdatedBy = _user.UserCode;
        p.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new PaymentTermDto(p.Id, p.Seq, p.Code, p.Description, p.NetDays, p.IsActive, p.CreatedOn);
    }
}
