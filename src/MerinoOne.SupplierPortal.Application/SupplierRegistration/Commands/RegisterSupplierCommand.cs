using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;
using SupplierAddressEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierAddress;
using SupplierContactEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierContact;

namespace MerinoOne.SupplierPortal.Application.SupplierRegistration.Commands;

public record RegisterSupplierCommand(SupplierRegistrationRequest Body) : IRequest<SupplierRegistrationResponse>;

public class RegisterSupplierCommandValidator : AbstractValidator<RegisterSupplierCommand>
{
    private static readonly Regex GstRegex = new("^[0-9A-Z]{15}$", RegexOptions.Compiled);
    // PAN: 5 letters + 4 digits + 1 letter
    private static readonly Regex PanRegex = new("^[A-Z]{5}[0-9]{4}[A-Z]{1}$", RegexOptions.Compiled);

    public RegisterSupplierCommandValidator()
    {
        RuleFor(x => x.Body.Token).NotEmpty();
        RuleFor(x => x.Body.LegalName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Body.SupplierType).NotEmpty()
            .Must(v => Enum.TryParse<SupplierType>(v, true, out _))
            .WithMessage("SupplierType must be one of: Material, Service, Both.");

        When(x => !string.IsNullOrWhiteSpace(x.Body.GstNumber), () =>
        {
            RuleFor(x => x.Body.GstNumber!).Matches(GstRegex)
                .WithMessage("GST number must be 15 alphanumeric characters.");
        });

        When(x => !string.IsNullOrWhiteSpace(x.Body.PanNumber), () =>
        {
            RuleFor(x => x.Body.PanNumber!).Matches(PanRegex)
                .WithMessage("PAN must be 5 letters + 4 digits + 1 letter (e.g., ABCDE1234F).");
        });

        RuleForEach(x => x.Body.Addresses).ChildRules(a =>
        {
            a.RuleFor(x => x.AddressType).NotEmpty().MaximumLength(50);
            a.RuleFor(x => x.Line1).NotEmpty().MaximumLength(200);
            a.RuleFor(x => x.City).NotEmpty().MaximumLength(100);
            a.RuleFor(x => x.State).NotEmpty().MaximumLength(100);
            a.RuleFor(x => x.PostalCode).NotEmpty().MaximumLength(20);
        });

        RuleForEach(x => x.Body.Contacts).ChildRules(c =>
        {
            c.RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
            c.RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        });

        // Mandatory document set:
        //   PAN + Cheque always required.
        //   GST iff GstNumber provided; MSME iff MsmeRegNumber provided.
        RuleFor(x => x.Body)
            .Must(HasDocumentOfType(DocumentType.OnboardingPan))
            .WithName("documents")
            .WithMessage("PAN card upload is required.");

        RuleFor(x => x.Body)
            .Must(HasDocumentOfType(DocumentType.OnboardingCheque))
            .WithName("documents")
            .WithMessage("Cancelled cheque upload is required.");

        When(x => !string.IsNullOrWhiteSpace(x.Body.GstNumber), () =>
        {
            RuleFor(x => x.Body)
                .Must(HasDocumentOfType(DocumentType.OnboardingGst))
                .WithName("documents")
                .WithMessage("GST certificate upload is required when a GST number is provided.");
        });

        When(x => !string.IsNullOrWhiteSpace(x.Body.MsmeRegNumber), () =>
        {
            RuleFor(x => x.Body)
                .Must(HasDocumentOfType(DocumentType.OnboardingMsme))
                .WithName("documents")
                .WithMessage("MSME certificate upload is required when an MSME registration number is provided.");
        });

        RuleForEach(x => x.Body.Documents).ChildRules(d =>
        {
            d.RuleFor(x => x.Id).NotEmpty()
                .WithMessage("Documents must reference an id returned by POST api/document-uploads.");
            d.RuleFor(x => x.DocumentType).NotEmpty()
                .Must(v => Enum.TryParse<DocumentType>(v, true, out _))
                .WithMessage("DocumentType must be one of the DocumentType enum values.");
        });
    }

    private static Func<SupplierRegistrationRequest, bool> HasDocumentOfType(DocumentType type)
        => req => req.Documents != null && req.Documents.Any(d =>
            Enum.TryParse<DocumentType>(d.DocumentType, true, out var parsed) && parsed == type);
}

public class RegisterSupplierCommandHandler : IRequestHandler<RegisterSupplierCommand, SupplierRegistrationResponse>
{
    private readonly IAppDbContext _db;
    private readonly IDocumentValidationService _docValidator;
    private readonly IEmailService _email;
    private readonly ILogger<RegisterSupplierCommandHandler> _logger;

    public RegisterSupplierCommandHandler(
        IAppDbContext db,
        IDocumentValidationService docValidator,
        IEmailService email,
        ILogger<RegisterSupplierCommandHandler> logger)
    {
        _db = db;
        _docValidator = docValidator;
        _email = email;
        _logger = logger;
    }

    public async Task<SupplierRegistrationResponse> Handle(RegisterSupplierCommand request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var token = request.Body.Token;

        var invite = await _db.SupplierInvites.FirstOrDefaultAsync(x => x.Token == token, ct)
                     ?? throw new NotFoundException("SupplierInvite", token);

        if (invite.ConsumedAt.HasValue)
            throw new ConflictException("This invite has already been used.");
        if (invite.ExpiresAt < now)
            throw new ConflictException("This invite has expired.");

        // Loose match: case-insensitive trim comparison
        if (!string.Equals(invite.LegalName.Trim(), request.Body.LegalName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new Common.Exceptions.ValidationException(new Dictionary<string, string[]>
            {
                ["legalName"] = new[] { "Legal name does not match the invite. Please use the company name from your invitation email." }
            });
        }

        // Compute next supplier code: S{N:D4} where N = max(numeric suffix of existing S#### codes) + 1
        var existingCodes = await _db.Suppliers.IgnoreQueryFilters()
            .Where(s => s.SupplierCode.StartsWith("S"))
            .Select(s => s.SupplierCode)
            .ToListAsync(ct);

        var maxSeq = 0;
        foreach (var c in existingCodes)
        {
            if (c.Length >= 2 && int.TryParse(c.AsSpan(1), out var n) && n > maxSeq)
                maxSeq = n;
        }
        var nextSeq = maxSeq + 1;
        var supplierCode = $"S{nextSeq:D4}";

        var supplierId = Guid.NewGuid();
        var seccodeId = Guid.NewGuid();

        // Scope (tenant + company) comes from the INVITE — registration is anonymous, so we never trust the
        // client for tenant/company. Resolve the parent TenantId from the invite's company when the invite's
        // own TenantId is absent (legacy invites). The created Supplier + its G-seccode both carry the scope.
        var inviteTenantId = invite.TenantId;
        var inviteCompanyId = invite.TenantEntityId;
        if (inviteCompanyId.HasValue && inviteTenantId is null)
        {
            inviteTenantId = await _db.TenantEntities.IgnoreQueryFilters()
                .Where(e => e.Id == inviteCompanyId.Value)
                .Select(e => e.TenantId)
                .FirstOrDefaultAsync(ct);
        }

        var seccode = new Seccode
        {
            Id = seccodeId,
            SeccodeType = SeccodeType.G,
            Name = $"{supplierCode} group",
            SupplierId = supplierId,
            TenantId = inviteTenantId,
            TenantEntityId = inviteCompanyId,
            CreatedBy = "self-register",
            CreatedOn = now,
        };
        _db.Seccodes.Add(seccode);

        var supplier = new SupplierEntity
        {
            Id = supplierId,
            TenantId = inviteTenantId,           // inherited from the invite (no client trust)
            TenantEntityId = inviteCompanyId,    // the supplier's single company
            SupplierCode = supplierCode,
            LegalName = request.Body.LegalName.Trim(),
            TradeName = string.IsNullOrWhiteSpace(request.Body.TradeName) ? null : request.Body.TradeName.Trim(),
            SupplierType = Enum.Parse<SupplierType>(request.Body.SupplierType, true),
            GstNumber = string.IsNullOrWhiteSpace(request.Body.GstNumber) ? null : request.Body.GstNumber.Trim().ToUpperInvariant(),
            PanNumber = string.IsNullOrWhiteSpace(request.Body.PanNumber) ? null : request.Body.PanNumber.Trim().ToUpperInvariant(),
            MsmeRegNumber = string.IsNullOrWhiteSpace(request.Body.MsmeRegNumber) ? null : request.Body.MsmeRegNumber.Trim(),
            Website = string.IsNullOrWhiteSpace(request.Body.Website) ? null : request.Body.Website.Trim(),
            RegistrationStatus = RegistrationStatus.Submitted,
            IsActiveSupplier = false,
            SeccodeId = seccodeId,
            InvitedBy = invite.InvitedBy,
            InvitedAt = invite.InvitedAt,
            CreatedBy = "self-register",
            CreatedOn = now,
        };

        foreach (var a in request.Body.Addresses ?? new List<SupplierAddressInput>())
        {
            supplier.Addresses.Add(new SupplierAddressEntity
            {
                Id = Guid.NewGuid(),
                SupplierId = supplierId,
                AddressType = a.AddressType.Trim(),
                AddressLine1 = a.Line1.Trim(),
                AddressLine2 = string.IsNullOrWhiteSpace(a.Line2) ? null : a.Line2.Trim(),
                City = a.City.Trim(),
                State = a.State.Trim(),
                Pincode = a.PostalCode.Trim(),
                Country = string.IsNullOrWhiteSpace(a.Country) ? "India" : a.Country.Trim(),
                CreatedBy = "self-register",
                CreatedOn = now,
            });
        }

        foreach (var c in request.Body.Contacts ?? new List<SupplierContactInput>())
        {
            supplier.Contacts.Add(new SupplierContactEntity
            {
                Id = Guid.NewGuid(),
                SupplierId = supplierId,
                ContactName = c.Name.Trim(),
                Designation = string.IsNullOrWhiteSpace(c.Designation) ? null : c.Designation.Trim(),
                Email = c.Email.Trim().ToLowerInvariant(),
                Phone = string.IsNullOrWhiteSpace(c.Phone) ? null : c.Phone.Trim(),
                IsPrimary = c.IsPrimary,
                CreatedBy = "self-register",
                CreatedOn = now,
            });
        }

        _db.Suppliers.Add(supplier);

        // Rewrite ownership of the already-uploaded onboarding documents from the
        // anonymous "PendingInvite" phase onto the new supplier + its seccode.
        // POST api/document-uploads created these rows with OwnerEntityType="PendingInvite"
        // and OwnerEntityId=invite.Id; we flip them here in the same EF transaction so
        // seccode filtering picks them up post-registration.
        var requestedIds = (request.Body.Documents ?? new List<UploadedDocumentInput>())
            .Select(d => d.Id).Distinct().ToList();
        // IgnoreQueryFilters — the upload endpoint runs anonymously and assigns each PendingInvite
        // doc an orphan invite-scope seccode; the registering user (also anonymous) can't see it
        // through the normal seccode filter. Token-bound ownership check below provides the gate.
        var pendingDocs = await _db.DocumentUploads
            .IgnoreQueryFilters()
            .Where(d => requestedIds.Contains(d.Id))
            .ToListAsync(ct);

        // Hard-fail if any requested id is missing or bound to a different invite — prevents
        // a malicious client from poisoning the new supplier with docs uploaded for someone else.
        foreach (var docId in requestedIds)
        {
            var doc = pendingDocs.FirstOrDefault(p => p.Id == docId);
            if (doc is null)
                throw new Common.Exceptions.ValidationException(new Dictionary<string, string[]>
                {
                    ["documents"] = new[] { $"Uploaded document {docId} was not found." }
                });
            if (doc.OwnerEntityType != "PendingInvite" || doc.OwnerEntityId != invite.Id)
                throw new Common.Exceptions.ValidationException(new Dictionary<string, string[]>
                {
                    ["documents"] = new[] { $"Uploaded document {docId} does not belong to this invite." }
                });
        }

        var docIds = pendingDocs.Select(d => d.Id).ToList();
        foreach (var doc in pendingDocs)
        {
            doc.OwnerEntityType = "Supplier";
            doc.OwnerEntityId = supplierId;
            doc.SeccodeId = seccodeId;
            doc.UpdatedBy = "self-register";
            doc.UpdatedOn = now;
        }

        // Consume the invite atomically (single SaveChanges = single EF transaction)
        invite.ConsumedAt = now;
        invite.SupplierId = supplierId;
        invite.UpdatedBy = "self-register";
        invite.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);

        // Kick off mock AI validation per upload. Mock returns in ~500ms — inline await is fine
        // and keeps the call deterministic for tests; an SLA-bound real service would dispatch
        // via the outbox/queue introduced in TSD §9.
        foreach (var docId in docIds)
        {
            try
            {
                var outcome = await _docValidator.ValidateAsync(docId, ct);
                var doc = await _db.DocumentUploads.FirstOrDefaultAsync(x => x.Id == docId, ct);
                if (doc != null)
                {
                    doc.AiValidationStatus = outcome.Status;
                    doc.AiValidationConfidence = outcome.Confidence;
                    doc.AiValidationPayload = outcome.Payload;
                    doc.AiValidatedAt = DateTime.UtcNow;
                    doc.UpdatedBy = "doc-validator";
                    doc.UpdatedOn = DateTime.UtcNow;
                }
            }
            catch
            {
                // Validation is best-effort; an error must not block self-registration.
            }
        }
        await _db.SaveChangesAsync(ct);

        // Fire-and-log acknowledgement email. Persistence already committed — log and continue
        // on send failure (must never roll the registration back). Recipient is the invite's
        // original address (the supplier already verified it via OTP); contactEmail in the body
        // is the primary contact's email from the registration form (may differ from invite.Email).
        try
        {
            var primaryContactEmail = supplier.Contacts.FirstOrDefault(c => c.IsPrimary)?.Email
                                      ?? invite.Email;
            await _email.SendRegistrationAcknowledgementAsync(
                invite.Email,
                supplier.LegalName,
                supplier.SupplierCode,
                primaryContactEmail,
                supplier.RegistrationStatus.ToString(),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Registration acknowledgement email send failed for {Email} (supplier {SupplierCode}). Registration was persisted successfully.",
                invite.Email, supplier.SupplierCode);
        }

        return new SupplierRegistrationResponse(
            supplierId,
            supplierCode,
            supplier.RegistrationStatus.ToString(),
            "Registration submitted. An administrator will review and approve your application.");
    }
}
