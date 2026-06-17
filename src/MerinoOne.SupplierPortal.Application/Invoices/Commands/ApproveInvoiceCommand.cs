using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

public record ApproveInvoiceCommand(Guid InvoiceId, ApproveInvoiceRequest Body) : IRequest<Unit>;

public class ApproveInvoiceCommandHandler : IRequestHandler<ApproveInvoiceCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public ApproveInvoiceCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<Unit> Handle(ApproveInvoiceCommand request, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.InvoiceId, ct)
                      ?? throw new NotFoundException("Invoice", request.InvoiceId);

        if (invoice.InvoiceStatus != InvoiceStatus.UnderReview &&
            invoice.InvoiceStatus != InvoiceStatus.Submitted &&
            invoice.InvoiceStatus != InvoiceStatus.Matched)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["invoiceStatus"] = new[] { $"Invoice cannot be approved from current state '{invoice.InvoiceStatus}'." }
            });
        }

        var now = DateTime.UtcNow;
        invoice.InvoiceStatus = InvoiceStatus.Approved;
        invoice.ApprovedBy = _user.UserCode;
        invoice.ApprovedAt = now;
        invoice.UpdatedBy = _user.UserCode;
        invoice.UpdatedOn = now;
        if (!string.IsNullOrWhiteSpace(request.Body.Notes))
            invoice.Notes = request.Body.Notes;

        // Inbound sync log entry — ERP returned approved status.
        _db.InforSyncLogs.Add(new InforSyncLog
        {
            Id = Guid.NewGuid(),
            EntityName = "Invoice",
            EntityId = invoice.Id.ToString(),
            Direction = SyncDirection.Inbound,
            Status = SyncStatus.Success,
            IdempotencyKey = $"invoice-approved:{invoice.Id}",
            SyncedAt = now,
            ErrorMessage = null,
            CreatedBy = _user.UserCode,
            CreatedOn = now,
        });

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
