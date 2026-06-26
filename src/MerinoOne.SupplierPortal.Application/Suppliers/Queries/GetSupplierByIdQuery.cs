using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
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
            .Include(x => x.BankDetails)
            .Include(x => x.Licenses)
            .Include(x => x.Currency)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("Supplier", request.Id);

        // Resolve bank-detail currency codes (Currency is ITenantOwned; IgnoreQueryFilters bypasses the company
        // filter — currencies are tenant-wide reference data, never company-scoped).
        var bankCurrencyIds = s.BankDetails.Select(b => b.CurrencyId).Distinct().ToList();
        var currencyCodes = bankCurrencyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Currencies.IgnoreQueryFilters()
                .Where(c => bankCurrencyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        // Documents — DocumentUpload has no nav back to Supplier (polymorphic owner), query directly.
        // IgnoreQueryFilters + re-apply !IsDeleted: DocumentUpload is ISeccode/ICompanyScoped, so the global
        // seccode+company filters would hide these from an internal reviewer (whose rights are NOT on the
        // supplier's G-seccode / whose active company differs) even though the supplier itself is already
        // access-gated above. Access to the documents follows access to their owning supplier (same as the
        // linkedUsers + bank-currency reads below).
        var docs = await _db.DocumentUploads
            .IgnoreQueryFilters()
            .Where(d => !d.IsDeleted && d.OwnerEntityType == "Supplier" && d.OwnerEntityId == s.Id)
            .OrderBy(d => d.DocumentType)
            .Select(d => new SupplierDocumentDto(
                d.Id,
                d.DocumentType,
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

        // Linked portal users — users mapped to this supplier via SupplierUserMap → SecRight. This is tenant-wide
        // admin config, so IgnoreQueryFilters (drop the company/seccode filters) and re-apply !IsDeleted; every linked
        // user shows regardless of the header's active company. The supplier itself is already tenant-scoped above.
        var linkedUsers = await (
            from m in _db.SupplierUserMaps.IgnoreQueryFilters().Where(m => m.SupplierId == s.Id && !m.IsDeleted)
            join u in _db.AppUsers.IgnoreQueryFilters().Where(u => !u.IsDeleted) on m.AppUserId equals u.Id
            join r in _db.SecRights.IgnoreQueryFilters().Where(r => !r.IsDeleted) on m.SecRightId equals r.Id into rj
            from r in rj.DefaultIfEmpty()
            orderby u.UserCode
            select new SupplierUserDto(
                u.Id, u.UserCode, u.FullName, u.Email, u.IsInternal, u.IsActive,
                r != null && r.CanWrite))
            .ToListAsync(ct);

        // License attachments — polymorphic doc.DocumentUpload rows (ownerEntityType='SupplierLicense').
        // One batched query keyed on the supplier's license ids (no per-license round-trip), grouped client-side.
        var licenseIds = s.Licenses.Select(l => l.Id).ToList();
        var attachmentsByLicense = new Dictionary<Guid, List<DocumentAttachmentDto>>();
        if (licenseIds.Count > 0)
        {
            var licenseDocs = await _db.DocumentUploads
                .IgnoreQueryFilters()
                .Where(d => !d.IsDeleted && d.OwnerEntityType == DocumentOwnerTypes.SupplierLicense && licenseIds.Contains(d.OwnerEntityId))
                .OrderBy(d => d.CreatedOn)
                .Select(d => new { d.OwnerEntityId, Dto = new DocumentAttachmentDto(
                    d.Id,
                    d.FileName,
                    d.MimeType,
                    d.FileSizeKb * 1024L, // stored in KB; DTO exposes bytes
                    d.CreatedOn,
                    // Base-href-relative (NO leading slash); the Web /files/proxy/{id} route forwards bearer auth
                    // to the authenticated GET /api/document-uploads/{id} download. Same endpoint serves preview
                    // (inline Content-Disposition) and download — the UI's <a download> drives the save flow.
                    $"files/proxy/{d.Id}",
                    $"files/proxy/{d.Id}") })
                .ToListAsync(ct);
            attachmentsByLicense = licenseDocs
                .GroupBy(x => x.OwnerEntityId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Dto).ToList());
        }

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
                new SupplierAddressDto(a.Id, a.AddressType, a.AddressLine1, a.AddressLine2, a.Area,
                    a.City, a.State, a.Pincode, a.Country)).ToList(),
            s.Contacts.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.ContactName).Select(c =>
                new SupplierContactDto(c.Id, c.ContactName, c.Designation, c.Email, c.Phone, c.IsPrimary)).ToList(),
            docs,
            inviteSummary,
            linkedUsers,
            s.BankDetails.OrderByDescending(b => b.IsPrimary).ThenBy(b => b.BankName).Select(b =>
                new SupplierBankDetailDto(
                    b.Id, b.Seq, b.SupplierId, b.BankName, b.BankAddress, b.AccountName, b.AccountNumber,
                    b.CurrencyId, currencyCodes.GetValueOrDefault(b.CurrencyId),
                    b.IfscCode, b.SwiftCode, b.IsPrimary, b.ErpCode, b.CreatedOn)).ToList(),
            s.Licenses.OrderBy(l => l.ExpiryDate).Select(l =>
                new SupplierLicenseDto(
                    l.Id, l.Seq, l.SupplierId, l.LicenseNumber, l.LicenseType, l.Remarks,
                    l.IssueDate, l.ExpiryDate, l.ErpCode, l.CreatedOn,
                    attachmentsByLicense.GetValueOrDefault(l.Id, new List<DocumentAttachmentDto>()))).ToList(),
            s.CurrencyId,
            s.Currency?.Code,
            s.PaymentTermId,
            s.PaymentTermCode,
            s.DeliveryTermId,
            s.DeliveryTermCode,
            s.PoConfirmationMode.ToString(),   // R4 (2026-06-26) — D1 (DTO field name kept as PoResponseMode).
            s.ErpCode,
            // R4 (2026-06-26) — Phase 5b / D1: the action toggles that travel with the confirmation mode.
            s.AllowNegotiate,
            s.AllowReject
        );
    }
}
