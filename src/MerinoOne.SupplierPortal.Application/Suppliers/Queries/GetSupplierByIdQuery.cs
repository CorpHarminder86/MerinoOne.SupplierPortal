using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Queries;

public record GetSupplierByIdQuery(Guid Id) : IRequest<SupplierDetailDto>;

public class GetSupplierByIdQueryHandler : IRequestHandler<GetSupplierByIdQuery, SupplierDetailDto>
{
    private readonly IAppDbContext _db;
    public GetSupplierByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<SupplierDetailDto> Handle(GetSupplierByIdQuery request, CancellationToken ct)
    {
        var s = await _db.Suppliers
            .Include(x => x.Verifications)
            .Include(x => x.Addresses)
            .Include(x => x.Contacts)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Supplier", request.Id);

        // Documents — DocumentUpload has no nav back to Supplier (polymorphic owner), query directly.
        var docs = await _db.DocumentUploads
            .Where(d => d.OwnerEntityType == "Supplier" && d.OwnerEntityId == s.Id)
            .OrderBy(d => d.DocumentType)
            .Select(d => new SupplierDocumentDto(
                d.Id,
                d.DocumentType.ToString(),
                d.FileName,
                d.FileSizeKb,
                d.MimeType,
                d.AiValidationStatus.ToString(),
                d.AiValidationConfidence,
                d.CreatedOn,
                // Base-href-relative (NO leading slash) so the browser resolves against the
                // Web app's <base href> — works for both root deploys ("/") and sub-path
                // deploys ("/sup-dev/"). Leading slash would bypass base-href and hit the
                // root domain, breaking sub-path hosting. The Web proxy injects bearer auth.
                $"files/proxy/{d.Id}"))
            .ToListAsync(ct);

        // InviteSummary — originating invite (1:1 via SupplierId).
        var now = DateTime.UtcNow;
        var inviteRow = await _db.SupplierInvites
            .Where(i => i.SupplierId == s.Id)
            .OrderByDescending(i => i.InvitedAt)
            .FirstOrDefaultAsync(ct);
        SupplierInviteSummaryDto? inviteSummary = null;
        if (inviteRow != null)
        {
            string status = inviteRow.ConsumedAt.HasValue
                ? "Consumed"
                : (inviteRow.CancelledAt.HasValue
                    ? "Cancelled"
                    : (inviteRow.ExpiresAt < now ? "Expired" : "Pending"));
            inviteSummary = new SupplierInviteSummaryDto(
                inviteRow.Id, inviteRow.LegalName, inviteRow.Email, inviteRow.MobileNo,
                inviteRow.InvitedBy, inviteRow.InvitedAt, inviteRow.ExpiresAt,
                inviteRow.ConsumedAt, inviteRow.CancelledAt, inviteRow.LastResentAt,
                inviteRow.ResendCount, status);
        }

        return new SupplierDetailDto(
            s.Id, s.Seq, s.SupplierCode, s.LegalName, s.TradeName,
            s.SupplierType.ToString(),
            s.GstNumber, s.PanNumber, s.MsmeRegNumber, s.MsmeCategory,
            s.GstValidated, s.PanValidated, s.MsmeValidated,
            s.RegistrationStatus.ToString(),
            s.InvitedBy, s.InvitedAt, s.ApprovedBy, s.ApprovedAt,
            s.ApprovalOverrideComment, s.RejectionReason, s.Website,
            s.IsActiveSupplier,
            s.Verifications.OrderByDescending(v => v.AttemptedAt).Select(v =>
                new SupplierVerificationDto(v.Id, v.VerificationType.ToString(), v.AttemptedAt,
                    v.AttemptedBy, v.ProviderName, v.Result.ToString(), v.Comments)).ToList(),
            s.Addresses.OrderBy(a => a.AddressType).Select(a =>
                new SupplierAddressDto(a.Id, a.AddressType, a.AddressLine1, a.AddressLine2,
                    a.City, a.State, a.Pincode, a.Country)).ToList(),
            s.Contacts.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.ContactName).Select(c =>
                new SupplierContactDto(c.Id, c.ContactName, c.Designation, c.Email, c.Phone, c.IsPrimary)).ToList(),
            docs,
            inviteSummary
        );
    }
}
