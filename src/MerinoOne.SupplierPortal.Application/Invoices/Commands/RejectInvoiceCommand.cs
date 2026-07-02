using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

public record RejectInvoiceCommand(Guid InvoiceId, RejectInvoiceRequest Body) : IRequest<Unit>;

public class RejectInvoiceCommandValidator : AbstractValidator<RejectInvoiceCommand>
{
    public RejectInvoiceCommandValidator()
    {
        RuleFor(x => x.Body.Reason).NotEmpty().MaximumLength(1000);
    }
}

public class RejectInvoiceCommandHandler : IRequestHandler<RejectInvoiceCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ILogger<RejectInvoiceCommandHandler> _logger;

    public RejectInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, ILogger<RejectInvoiceCommandHandler> logger)
    {
        _db = db; _user = user; _logger = logger;
    }

    public async Task<Unit> Handle(RejectInvoiceCommand request, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.InvoiceId, ct)
                      ?? throw new NotFoundException("Invoice", request.InvoiceId);

        if (invoice.InvoiceStatus == InvoiceStatus.Approved ||
            invoice.InvoiceStatus == InvoiceStatus.Paid ||
            invoice.InvoiceStatus == InvoiceStatus.PartiallyPaid ||
            invoice.InvoiceStatus == InvoiceStatus.Cancelled)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["invoiceStatus"] = new[] { $"Invoice cannot be rejected from current state '{invoice.InvoiceStatus}'." }
            });
        }

        var priorStatus = invoice.InvoiceStatus;

        invoice.InvoiceStatus = InvoiceStatus.Rejected;
        invoice.RejectionReason = request.Body.Reason;
        invoice.UpdatedBy = _user.UserCode;
        invoice.UpdatedOn = DateTime.UtcNow;

        // R6 (plan D8) — rejecting an invoice out of a reservation-holding state releases its per-PO-line
        // invoiced-qty reservation (compensating negative conditional ExecuteUpdate). One transaction so the
        // release and the status flip commit (or roll back) together.
        await using var tx = await _db.BeginTransactionAsync(ct);
        if (InvoiceReservationRelease.HoldsReservation(priorStatus))
            await InvoiceReservationRelease.ReleaseAsync(_db, invoice.Id, _logger, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Unit.Value;
    }
}
