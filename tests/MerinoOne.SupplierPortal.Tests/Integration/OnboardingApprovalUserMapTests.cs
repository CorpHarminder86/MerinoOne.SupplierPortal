using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Onboarding → approval → user-provisioning chain through the REAL host, including the C7 security regression.
///
/// The flow under test:
///   1. Admin POSTs an invite (POST /api/supplier-registration/invites) for email X under the fixture company.
///   2. The prospect uploads the mandatory PAN + Cheque docs against the invite token (anonymous multipart),
///      then POSTs /api/supplier-registration/register with primary-contact email == X.
///   3. Admin approves (POST /api/suppliers/{id}/approve).
///
/// Assertions:
///   • <b>Provision</b> — when no portal user owns X, approval auto-provisions a fresh user + a SupplierUserMap.
///   • <b>Link</b> — when an ACTIVE user already owns the invite email X, approval LINKS that existing user
///     (a SupplierUserMap to it; no second user created).
///   • <b>C7 security</b> — when the primary-contact email != the OTP-verified invite email AND a DIFFERENT
///     existing active user owns that contact email, approval must NOT link that arbitrary "victim" user
///     (no SupplierUserMap to the victim; the inviteBound guard in ApproveSupplierCommandHandler).
///   • <b>Validator</b> — a register call missing the mandatory Cheque upload is a 400 (FluentValidation), and a
///     license row missing its number/type is a 400 — proving the mandatory-doc + license rules are enforced.
///
/// Every test mints a UNIQUE email / legal-name / tag so it never collides with the shared seed or another test.
/// Money-path: scope gate stays OFF (no flip).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class OnboardingApprovalUserMapTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public OnboardingApprovalUserMapTests(IntegrationTestFixture fx) => _fx = fx;

    // -------------------- happy path: new user provisioned on approve --------------------

    [SkippableFact]
    public async Task Approve_provisions_a_new_user_and_supplier_user_map_when_email_is_unknown()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var legalName = $"Onboard New {tag} Pvt Ltd";
        var email = $"onboard-new-{tag}@supplier.test";

        var (invite, token) = await CreateInviteAsync(legalName, email);
        var supplierId = await RegisterAsync(token, legalName, primaryContactEmail: email);

        await ApproveAsync(supplierId);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The supplier is now Approved + active.
        var supplier = await db.Suppliers.IgnoreQueryFilters().FirstAsync(s => s.Id == supplierId);
        supplier.RegistrationStatus.Should().Be(RegistrationStatus.Approved);

        // A user with the contact email was provisioned (it did not exist before).
        var user = await db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == email.ToLowerInvariant() && !u.IsDeleted);
        user.Should().NotBeNull(because: "approval auto-provisions the primary contact as a portal user");

        // A SupplierUserMap binds that user to the new supplier.
        var map = await db.SupplierUserMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.SupplierId == supplierId && m.AppUserId == user!.Id && !m.IsDeleted);
        map.Should().NotBeNull(because: "the provisioned user must be mapped to its supplier (canRead, canWrite=false)");
    }

    // -------------------- existing active user is linked (not duplicated) --------------------

    [SkippableFact]
    public async Task Approve_links_an_existing_active_user_who_owns_the_invite_email()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var legalName = $"Onboard Link {tag} Pvt Ltd";
        var email = $"onboard-link-{tag}@supplier.test";

        // Pre-seed an ACTIVE user that already owns the invite email.
        var existingUserId = await SeedActiveUserAsync($"link-{tag}", email);

        var (_, token) = await CreateInviteAsync(legalName, email);
        // Primary contact email == invite email == the existing user's email → inviteBound true.
        var supplierId = await RegisterAsync(token, legalName, primaryContactEmail: email);

        await ApproveAsync(supplierId);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The pre-existing user is LINKED — a SupplierUserMap to it now exists.
        var map = await db.SupplierUserMaps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.SupplierId == supplierId && m.AppUserId == existingUserId && !m.IsDeleted);
        map.Should().NotBeNull(because: "an active user owning the OTP-verified invite email is linked on approve (R4)");

        // No SECOND user was provisioned for that email.
        var usersWithEmail = await db.AppUsers.IgnoreQueryFilters()
            .CountAsync(u => u.Email == email.ToLowerInvariant() && !u.IsDeleted);
        usersWithEmail.Should().Be(1, because: "the existing user is reused, not duplicated");
    }

    // -------------------- C7 SECURITY: an arbitrary victim is never linked --------------------

    [SkippableFact]
    public async Task Approve_does_not_link_an_arbitrary_victim_when_contact_email_differs_from_invite()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var legalName = $"Onboard Attack {tag} Pvt Ltd";
        var inviteEmail = $"onboard-invite-{tag}@supplier.test";          // chosen by the inviting admin
        var victimEmail = $"onboard-victim-{tag}@internal.test";          // a DIFFERENT existing active user

        // Pre-seed the VICTIM — an active user the self-registrant must NOT be able to bind to their supplier.
        var victimUserId = await SeedActiveUserAsync($"victim-{tag}", victimEmail);

        var (_, token) = await CreateInviteAsync(legalName, inviteEmail);
        // The self-registrant puts the VICTIM's email as the primary contact (!= the invite email).
        var supplierId = await RegisterAsync(token, legalName, primaryContactEmail: victimEmail);

        await ApproveAsync(supplierId);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // C7 — the victim must NOT have been linked to the attacker's supplier (inviteBound guard).
        var victimMap = await db.SupplierUserMaps.IgnoreQueryFilters()
            .AnyAsync(m => m.SupplierId == supplierId && m.AppUserId == victimUserId && !m.IsDeleted);
        victimMap.Should().BeFalse(
            because: "the primary-contact email != the OTP-verified invite email, so the existing victim user must NOT be auto-linked (C7)");

        // And no SecRight on the supplier's seccode was granted to the victim's user code, either.
        var supplier = await db.Suppliers.IgnoreQueryFilters().FirstAsync(s => s.Id == supplierId);
        var victimUserCode = await db.AppUsers.IgnoreQueryFilters()
            .Where(u => u.Id == victimUserId).Select(u => u.UserCode).FirstAsync();
        var victimSecRight = await db.SecRights.IgnoreQueryFilters()
            .AnyAsync(r => r.SeccodeId == supplier.SeccodeId && r.UserCode == victimUserCode && !r.IsDeleted);
        victimSecRight.Should().BeFalse(because: "no row-level grant on the attacker's supplier may leak to the victim");
    }

    // -------------------- validator: the mandatory-doc + license rules return 400 --------------------

    [SkippableFact]
    public async Task Register_without_the_mandatory_cheque_upload_is_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var legalName = $"Onboard NoCheque {tag} Pvt Ltd";
        var email = $"onboard-nocheque-{tag}@supplier.test";

        var (_, token) = await CreateInviteAsync(legalName, email);

        // Upload ONLY the PAN doc — deliberately omit the mandatory Cheque.
        var panId = await UploadDocAsync(token, "OnboardingPan", "pan.pdf");

        var body = BuildRegistrationRequest(token, legalName, email,
            documents: new List<UploadedDocumentInput>
            {
                new(panId, nameof(DocumentType.OnboardingPan), "pan.pdf", "files/x", 1, "application/pdf"),
            });

        var anon = _fx.Factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/supplier-registration/register", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the cancelled-cheque upload is mandatory (RegisterSupplierCommandValidator)");
        var result = await Read<SupplierRegistrationResponse>(resp);
        result.Success.Should().BeFalse();
        string.Join(" ", result.Errors).ToLowerInvariant().Should().Contain("cheque",
            because: "the validator surfaces the missing-cheque rule");
    }

    [SkippableFact]
    public async Task Register_with_a_license_missing_its_number_is_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var legalName = $"Onboard BadLicense {tag} Pvt Ltd";
        var email = $"onboard-badlic-{tag}@supplier.test";

        var (_, token) = await CreateInviteAsync(legalName, email);
        var panId = await UploadDocAsync(token, "OnboardingPan", "pan.pdf");
        var chequeId = await UploadDocAsync(token, "OnboardingCheque", "cheque.pdf");

        var body = BuildRegistrationRequest(token, legalName, email,
            documents: new List<UploadedDocumentInput>
            {
                new(panId, nameof(DocumentType.OnboardingPan), "pan.pdf", "files/x", 1, "application/pdf"),
                new(chequeId, nameof(DocumentType.OnboardingCheque), "cheque.pdf", "files/y", 1, "application/pdf"),
            },
            // A license row violating the validator: empty number + empty type.
            licenses: new List<SupplierLicenseInput> { new(LicenseNumber: "", LicenseType: "") });

        var anon = _fx.Factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/supplier-registration/register", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a license row must carry a non-empty LicenseNumber + LicenseType (RegisterSupplierCommandValidator)");
    }

    // ====================================================================================================
    // Flow helpers — each makes its own uniquely-tagged data via the real HTTP surface.
    // ====================================================================================================

    /// <summary>Admin creates an invite for the given email under the fixture company; returns (invite, token).</summary>
    private async Task<(SupplierInviteDetailDto Invite, string Token)> CreateInviteAsync(string legalName, string email)
    {
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var req = new CreateSupplierInviteRequest(legalName, email, IntegrationTestFixture.CompanyId);
        var resp = await admin.PostAsJsonAsync("/api/supplier-registration/invites", req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var result = await Read<CreateSupplierInviteResponse>(resp);
        result.Success.Should().BeTrue(because: await Body(resp));
        return (result.Data!.Invite, result.Data!.Token);
    }

    /// <summary>
    /// Uploads PAN + Cheque against the token, then registers with the given primary-contact email; returns the
    /// new supplier id. The legal name must match the invite (the register handler enforces it).
    /// </summary>
    private async Task<Guid> RegisterAsync(string token, string legalName, string primaryContactEmail)
    {
        var panId = await UploadDocAsync(token, "OnboardingPan", "pan.pdf");
        var chequeId = await UploadDocAsync(token, "OnboardingCheque", "cheque.pdf");

        var body = BuildRegistrationRequest(token, legalName, primaryContactEmail,
            documents: new List<UploadedDocumentInput>
            {
                new(panId, nameof(DocumentType.OnboardingPan), "pan.pdf", "files/x", 1, "application/pdf"),
                new(chequeId, nameof(DocumentType.OnboardingCheque), "cheque.pdf", "files/y", 1, "application/pdf"),
            });

        var anon = _fx.Factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/supplier-registration/register", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var result = await Read<SupplierRegistrationResponse>(resp);
        result.Success.Should().BeTrue(because: await Body(resp));
        return result.Data!.SupplierId;
    }

    private static SupplierRegistrationRequest BuildRegistrationRequest(
        string token, string legalName, string primaryContactEmail,
        List<UploadedDocumentInput> documents, List<SupplierLicenseInput>? licenses = null)
        => new(
            Token: token,
            LegalName: legalName,
            TradeName: null,
            SupplierType: nameof(SupplierType.Material),
            GstNumber: null,
            PanNumber: "ABCDE1234F",
            MsmeRegNumber: null,
            Website: "https://example.test",
            Addresses: new List<SupplierAddressInput>
            {
                new("Registered", "1 Test Street", null, null, "Mumbai", "Maharashtra", "400001", "India"),
            },
            Contacts: new List<SupplierContactInput>
            {
                new(Name: "Primary Person", Designation: "Director", Email: primaryContactEmail, Phone: "9999999999", IsPrimary: true),
            },
            Documents: documents,
            Licenses: licenses);

    /// <summary>Anonymous multipart upload bound to the invite token. Returns the new document id.</summary>
    private async Task<Guid> UploadDocAsync(string token, string documentType, string fileName)
    {
        var anon = _fx.Factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var bytes = Encoding.UTF8.GetBytes($"%PDF-1.4 test {Guid.NewGuid():N}");
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(documentType), "documentType");
        form.Add(new StringContent(token), "token");

        var resp = await anon.PostAsync("/api/document-uploads", form);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        var result = await Read<UploadedDocumentDto>(resp);
        result.Success.Should().BeTrue(because: await Body(resp));
        return result.Data!.Id;
    }

    private async Task ApproveAsync(Guid supplierId)
    {
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var resp = await admin.PostAsJsonAsync($"/api/suppliers/{supplierId}/approve", new ApproveSupplierRequest("ok"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
    }

    /// <summary>Seeds an ACTIVE, non-MFA portal user with the given email directly via the DbContext.</summary>
    private async Task<Guid> SeedActiveUserAsync(string userCode, string email)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        db.AppUsers.Add(new AppUser
        {
            Id = userId,
            UserCode = userCode,
            FullName = $"User {userCode}",
            Email = email.ToLowerInvariant(),
            PasswordHash = PasswordHasher.DeterministicHash(SecurityTestHarness.Password),
            IsInternal = false,
            IsMfaEnabled = false,
            IsActive = true,
            TenantId = IntegrationTestFixture.TenantId,
            CreatedBy = "seed",
            CreatedOn = now,
        });
        await db.SaveChangesAsync();
        return userId;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
