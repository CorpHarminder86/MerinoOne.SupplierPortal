using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Application.SystemSettings.Scope;
using MerinoOne.SupplierPortal.Contracts.Auth;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Tests.Infrastructure;

/// <summary>
/// Phase-1 harness extensions for the security + RLS suites. Layered ON TOP of the existing
/// <see cref="IntegrationTestFixture"/> money-path seed (which it never mutates):
/// <list type="bullet">
///   <item>Roles + Permissions + RolePermissions (idempotent; reuses the production <see cref="PermissionSeeder"/>)
///         so the permission-backed policies actually resolve in the test DB.</item>
///   <item>Three login-able, NON-MFA users under the fixture tenant: an Admin (holds Supplier.Approve via the
///         Admin role), a Supplier-role user (no Supplier.Approve), and a Buyer (no Supplier.Approve). Each
///         carries the tenant + a UserCompanyMap to company "2000", so the minted JWT gets tenant/company claims.</item>
///   <item>A SECOND tenant (+ company + supplier + an inbound sync-log + a DocumentUpload) so cross-tenant
///         isolation can be asserted from a tenant-A principal.</item>
/// </list>
///
/// <para><b>Token minting</b> goes through the real <c>POST /api/auth/login</c> so the whole permission
/// resolution chain (UserRole → RolePermission → Permission → "permission" claim → PermissionRequirement)
/// is exercised end-to-end — exactly what the AuthZ regressions need to lock in.</para>
///
/// <para><b>Gate tension</b>: the <c>Scope.FiltersEnabled</c> gate is a single global SystemSetting behind a
/// singleton cache (<see cref="IScopeFilterGate"/>). The fixture's money-path tests run with the gate OFF
/// (the backfill window). The RLS tests need it ON. Because every integration test shares ONE xUnit collection
/// (serial execution against the one DB), a test flips the gate ON only for its own duration via
/// <see cref="EnableScopeFiltersAsync"/> and ALWAYS restores OFF + invalidates the cache in a finally. No test
/// runs concurrently with the flip, so the money-path tests never observe the gate ON.</para>
/// </summary>
public static class SecurityTestHarness
{
    public const string Password = "Sec@Test123";

    // --- Tenant-A (the fixture tenant) login-able users -------------------------------------------------
    public static class Users
    {
        public const string Admin      = "sec-admin-a";       // role Admin    → HAS Supplier.Approve
        public const string Supplier   = "sec-supplier-a";    // role Supplier → NO Supplier.Approve
        public const string Buyer      = "sec-buyer-a";       // role Buyer    → NO Supplier.Approve
        public const string AdminB     = "sec-admin-b";       // role Admin under tenant B (cross-tenant principal)
        // R4 (2026-06-26) — UC-PO-09: the gate-override path needs ONE principal holding BOTH Asn.Write AND
        // PurchaseOrder.OverrideGate. Neither Admin (no Asn.Write) nor Supplier (no OverrideGate) alone qualifies;
        // SuperAdmin holds both, so a SuperAdmin-role user under the fixture tenant drives the audited override.
        public const string SuperAdmin = "sec-superadmin-a";  // role SuperAdmin → HAS Asn.Write + OverrideGate
    }

    public static readonly Guid AdminUserId      = Det("sec.user", Users.Admin);
    public static readonly Guid SupplierUserId   = Det("sec.user", Users.Supplier);
    public static readonly Guid BuyerUserId      = Det("sec.user", Users.Buyer);
    public static readonly Guid AdminBUserId     = Det("sec.user", Users.AdminB);
    public static readonly Guid SuperAdminUserId = Det("sec.user", Users.SuperAdmin);

    // --- Tenant B (the foreign tenant for cross-tenant isolation) ---------------------------------------
    public static readonly Guid TenantBId        = Det("sec.tenant", "B");
    public static readonly Guid CompanyBId       = Det("sec.company", "B-9000");
    public const string        CompanyBCode      = "9000";
    public static readonly Guid SupplierBSeccode = Det("sec.seccode", "B-supplier");
    public static readonly Guid SupplierBId      = Det("sec.supplier", "B");
    public const string        SupplierBCode     = "SUP-TENB-01";
    public static readonly Guid SyncLogBId       = Det("sec.synclog", "B");
    public static readonly Guid DocBId           = Det("sec.doc", "B");

    private static Guid Det(string ns, string key) => DeterministicId.From(ns, key);

    // ====================================================================================================
    // Seed (idempotent). Called once from the fixture AFTER its own money-path seed.
    // ====================================================================================================
    public static async Task SeedAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;

        // 0. Defensively pin the scope gate OFF at seed time (the money-path backfill window). If a previous
        // crashed run leaked the gate ON in the persistent test DB, this resets it so the money-path tests
        // start from the expected OFF state. The RLS tests flip it ON only transiently (and restore OFF).
        var gate = await db.SystemSettings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Category == ScopeFilterGate.Category && s.SettingKey == ScopeFilterGate.Key);
        if (gate is null)
            db.SystemSettings.Add(new Domain.Entities.Settings.SystemSetting
            {
                Id = Det("sec.setting", "scope-gate"),
                Category = ScopeFilterGate.Category, SettingKey = ScopeFilterGate.Key, SettingValue = "false",
                IsActive = true, CreatedBy = "seed", CreatedOn = now
            });
        else if (!string.Equals(gate.SettingValue, "false", StringComparison.OrdinalIgnoreCase))
        {
            gate.SettingValue = "false"; gate.UpdatedBy = "seed"; gate.UpdatedOn = now;
        }
        await db.SaveChangesAsync();

        // 1. Roles + Permissions + RolePermissions (production seeder; idempotent + safe to re-run).
        await PermissionSeeder.SeedAsync(db);

        var roleMap = await db.Roles.IgnoreQueryFilters().ToDictionaryAsync(r => r.Name, r => r.Id);

        // 2. Tenant-A users (under the fixture tenant + fixture company "2000").
        await SeedUserAsync(db, AdminUserId,    Users.Admin,    "Sec Admin A",    "sec-admin-a@merino.local",
            "Admin",    IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, roleMap, now);
        await SeedUserAsync(db, SupplierUserId, Users.Supplier, "Sec Supplier A", "sec-supplier-a@merino.local",
            "Supplier", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, roleMap, now);
        await SeedUserAsync(db, BuyerUserId,    Users.Buyer,    "Sec Buyer A",    "sec-buyer-a@merino.local",
            "Buyer",    IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, roleMap, now);
        await SeedUserAsync(db, SuperAdminUserId, Users.SuperAdmin, "Sec SuperAdmin A", "sec-superadmin-a@merino.local",
            "SuperAdmin", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, roleMap, now);

        // Give the Supplier-role user CanRead on the fixture supplier's G-seccode so it is NOT a no-rows
        // principal when the seccode filter enforces (used as a sanity baseline; not strictly required).
        if (!await db.SecRights.IgnoreQueryFilters().AnyAsync(r =>
                r.SeccodeId == IntegrationTestFixture.SeccodeId && r.UserCode == Users.Supplier))
        {
            db.SecRights.Add(new SecRight
            {
                Id = Det("sec.secright", $"{Users.Supplier}|fixtureG"),
                SeccodeId = IntegrationTestFixture.SeccodeId,
                UserCode = Users.Supplier,
                CanRead = true,
                CanWrite = false,
                CreatedBy = "seed",
                CreatedOn = now,
            });
            await db.SaveChangesAsync();
        }

        // 3. Second tenant B + company + supplier + sync-log + document (foreign data).
        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == TenantBId))
        {
            db.Tenants.Add(new Tenant { Id = TenantBId, Name = "Sec Tenant B", IsActive = true, CreatedBy = "seed", CreatedOn = now });
            await db.SaveChangesAsync();
        }
        if (!await db.TenantEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == CompanyBId))
        {
            db.TenantEntities.Add(new TenantEntity
            {
                Id = CompanyBId, TenantId = TenantBId, Code = CompanyBCode, Name = "Sec Co 9000",
                IsActive = true, CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }
        if (!await db.Seccodes.IgnoreQueryFilters().AnyAsync(s => s.Id == SupplierBSeccode))
        {
            db.Seccodes.Add(new Seccode
            {
                Id = SupplierBSeccode, SeccodeType = SeccodeType.G, Name = "Sec tenant-B supplier seccode",
                SupplierId = SupplierBId, TenantId = TenantBId, TenantEntityId = CompanyBId,
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }
        if (!await db.Suppliers.IgnoreQueryFilters().AnyAsync(s => s.Id == SupplierBId))
        {
            db.Suppliers.Add(new SupplierEntity
            {
                Id = SupplierBId, SupplierCode = SupplierBCode, LegalName = "Tenant B Supplier Pvt Ltd",
                SupplierType = SupplierType.Material, RegistrationStatus = RegistrationStatus.Active,
                IsActiveSupplier = true, SeccodeId = SupplierBSeccode, TenantId = TenantBId, TenantEntityId = CompanyBId,
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }
        // Foreign inbound sync-log row (tenant B). C8 asserts a tenant-A principal can't read it.
        if (!await db.InforSyncLogs.IgnoreQueryFilters().AnyAsync(l => l.Id == SyncLogBId))
        {
            db.InforSyncLogs.Add(new InforSyncLog
            {
                Id = SyncLogBId, TenantId = TenantBId, EntityName = nameof(TransactionalInboundEntity.Grn),
                Direction = SyncDirection.Inbound, Status = SyncStatus.Success, SyncedAt = now,
                EntityCount = 1, IdempotencyKey = "sec-tenantB-synclog",
                PayloadJson = "{\"secret\":\"tenant-B-only\"}",
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }
        // Tenant-B admin (cross-tenant principal). Seeded AFTER tenant B + company B exist.
        await SeedUserAsync(db, AdminBUserId, Users.AdminB, "Sec Admin B", "sec-admin-b@merino.local",
            "Admin", TenantBId, CompanyBId, roleMap, now);

        // Foreign DocumentUpload row (tenant B). C6 asserts a tenant-A internal user gets 404, not the file.
        if (!await db.DocumentUploads.IgnoreQueryFilters().AnyAsync(d => d.Id == DocBId))
        {
            db.DocumentUploads.Add(new DocumentUpload
            {
                Id = DocBId, OwnerEntityType = "Supplier", OwnerEntityId = SupplierBId,
                DocumentType = nameof(DocumentType.License), FileName = "tenantB-secret.pdf",
                FileUrl = "tenantB/secret.pdf", FileSizeKb = 1, MimeType = "application/pdf",
                UploadedBy = "seed", SeccodeId = SupplierBSeccode, TenantId = TenantBId, TenantEntityId = CompanyBId,
                AiValidationStatus = AiValidationStatus.Pending, CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedUserAsync(
        AppDbContext db, Guid userId, string userCode, string fullName, string email, string role,
        Guid tenantId, Guid companyId, IReadOnlyDictionary<string, Guid> roleMap, DateTime now)
    {
        if (!await db.AppUsers.IgnoreQueryFilters().AnyAsync(u => u.Id == userId))
        {
            db.AppUsers.Add(new AppUser
            {
                Id = userId, UserCode = userCode, FullName = fullName, Email = email,
                // NON-MFA so /api/auth/login mints the JWT in one leg.
                PasswordHash = PasswordHasher.DeterministicHash(Password),
                IsInternal = role != "Supplier", IsMfaEnabled = false, IsActive = true,
                TenantId = tenantId, CreatedBy = "seed", CreatedOn = now
            });

            if (roleMap.TryGetValue(role, out var roleId))
                db.UserRoles.Add(new UserRole
                {
                    Id = Det("sec.userrole", $"{userCode}|{role}"),
                    AppUserId = userId, RoleId = roleId, CreatedBy = "seed", CreatedOn = now
                });

            // Type-U seccode + self SecRight (mirrors UserSeeder).
            var seccodeId = Det("sec.seccode.u", userCode);
            db.Seccodes.Add(new Seccode
            {
                Id = seccodeId, SeccodeType = SeccodeType.U, Name = userCode + " default",
                AppUserId = userId, TenantId = tenantId, CreatedBy = "seed", CreatedOn = now
            });
            db.SecRights.Add(new SecRight
            {
                Id = Det("sec.secright.u", userCode), SeccodeId = seccodeId, UserCode = userCode,
                CanRead = true, CanWrite = true, CreatedBy = "seed", CreatedOn = now
            });

            await db.SaveChangesAsync();
        }

        // Company map (gives the JWT a "company" claim + lets the company filter resolve an active company).
        if (!await db.UserCompanyMaps.IgnoreQueryFilters().AnyAsync(m => m.AppUserId == userId && m.TenantEntityId == companyId))
        {
            db.UserCompanyMaps.Add(new UserCompanyMap
            {
                Id = Det("sec.ucm", $"{userCode}|{companyId}"),
                AppUserId = userId, TenantEntityId = companyId, TenantId = tenantId,
                IsDefault = true, AllSuppliers = false, CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }
    }

    // ====================================================================================================
    // Data builders (reusable by later suites). Every row carries a UNIQUE tag so suites never collide.
    // ====================================================================================================

    /// <summary>A supplier + its G-seccode created under <paramref name="tenantId"/>/<paramref name="companyId"/>.</summary>
    public sealed record SeededSupplier(Guid SupplierId, Guid SeccodeId, string SupplierCode);

    /// <summary>
    /// Creates a tagged supplier (its own G-seccode) under the given tenant/company. Use a per-test
    /// <paramref name="tag"/> (e.g. a GUID:N) so the row never collides with the shared seed or another test.
    /// Optionally grants a user a SecRight (canRead/canWrite) on the new supplier's seccode.
    /// </summary>
    public static async Task<SeededSupplier> CreateSupplierAsync(
        this IntegrationTestFixture fx, string tag, Guid tenantId, Guid companyId,
        string? grantUserCode = null, bool canWrite = false)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var seccodeId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var code = $"SUP-{tag}";

        db.Seccodes.Add(new Seccode
        {
            Id = seccodeId, SeccodeType = SeccodeType.G, Name = $"sec-test {tag}",
            SupplierId = supplierId, TenantId = tenantId, TenantEntityId = companyId,
            CreatedBy = "seed", CreatedOn = now
        });
        db.Suppliers.Add(new SupplierEntity
        {
            Id = supplierId, SupplierCode = code, LegalName = $"Test Supplier {tag}",
            SupplierType = SupplierType.Material, RegistrationStatus = RegistrationStatus.Submitted,
            IsActiveSupplier = false, SeccodeId = seccodeId, TenantId = tenantId, TenantEntityId = companyId,
            CreatedBy = "seed", CreatedOn = now
        });
        if (!string.IsNullOrEmpty(grantUserCode))
        {
            db.SecRights.Add(new SecRight
            {
                Id = Guid.NewGuid(), SeccodeId = seccodeId, UserCode = grantUserCode,
                CanRead = true, CanWrite = canWrite, CreatedBy = "seed", CreatedOn = now
            });
        }
        await db.SaveChangesAsync();
        return new SeededSupplier(supplierId, seccodeId, code);
    }

    /// <summary>Inserts a tagged inbound sync-log row for the given tenant (reusable by later suites).</summary>
    public static async Task<Guid> CreateInboundSyncLogAsync(this IntegrationTestFixture fx, string tag, Guid tenantId)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        db.InforSyncLogs.Add(new InforSyncLog
        {
            Id = id, TenantId = tenantId, EntityName = nameof(TransactionalInboundEntity.Grn),
            Direction = SyncDirection.Inbound, Status = SyncStatus.Success, SyncedAt = now,
            EntityCount = 1, IdempotencyKey = $"sec-{tag}", PayloadJson = $"{{\"tag\":\"{tag}\"}}",
            CreatedBy = "seed", CreatedOn = now
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ====================================================================================================
    // Token / client helpers
    // ====================================================================================================
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Logs the given seeded user in via the real /api/auth/login and returns the bearer token.</summary>
    public static async Task<string> TokenForAsync(this IntegrationTestFixture fx, string userCode)
    {
        var client = fx.Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userCode, Password));
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"login failed for '{userCode}': {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        var stream = await resp.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<Result<LoginResponse>>(stream, Json)
                     ?? throw new InvalidOperationException("null login response");
        if (result.Data is null || string.IsNullOrEmpty(result.Data.Token))
            throw new InvalidOperationException($"login for '{userCode}' returned no token (RequiresMfa={result.Data?.RequiresMfa}).");
        return result.Data.Token;
    }

    /// <summary>
    /// An <see cref="HttpClient"/> authorized as the given seeded user. The optional active-company header
    /// drives the always-on company filter (set it to the company whose rows you expect to see).
    /// </summary>
    public static async Task<HttpClient> ClientAsAsync(this IntegrationTestFixture fx, string userCode, Guid? activeCompanyId = null)
    {
        var token = await fx.TokenForAsync(userCode);
        var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (activeCompanyId.HasValue)
            client.DefaultRequestHeaders.Add("X-Active-Company", activeCompanyId.Value.ToString());
        return client;
    }

    // ====================================================================================================
    // Scope-filter gate flip (RLS tests only). ALWAYS pair the enable with the returned disposable so the
    // gate is restored OFF + the singleton cache invalidated, keeping the money-path tests on gate-OFF.
    // ====================================================================================================

    /// <summary>
    /// Flips <c>Scope.FiltersEnabled</c> to "true" in the test DB and invalidates the singleton gate cache,
    /// so the tenant + company query filters ENFORCE. Returns a disposable that restores the setting to
    /// "false" and re-invalidates the cache. Serial collection execution guarantees no other test observes
    /// the ON state.
    /// </summary>
    public static async Task<IAsyncDisposable> EnableScopeFiltersAsync(this IntegrationTestFixture fx)
    {
        await SetScopeGateAsync(fx, enabled: true);
        return new ScopeGateScope(fx);
    }

    private static async Task SetScopeGateAsync(IntegrationTestFixture fx, bool enabled)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var setting = await db.SystemSettings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Category == ScopeFilterGate.Category && s.SettingKey == ScopeFilterGate.Key);
        var value = enabled ? "true" : "false";
        if (setting is null)
        {
            db.SystemSettings.Add(new Domain.Entities.Settings.SystemSetting
            {
                Id = Det("sec.setting", "scope-gate"),
                Category = ScopeFilterGate.Category, SettingKey = ScopeFilterGate.Key, SettingValue = value,
                IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            });
        }
        else
        {
            setting.SettingValue = value;
            setting.UpdatedBy = "seed";
            setting.UpdatedOn = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        // Drop the cached singleton snapshot so the next query re-reads the new value.
        foreach (var inv in fx.Factory.Services.GetServices<ISettingsCacheInvalidator>())
            inv.InvalidateCategory(ScopeFilterGate.Category);
    }

    private sealed class ScopeGateScope : IAsyncDisposable
    {
        private readonly IntegrationTestFixture _fx;
        public ScopeGateScope(IntegrationTestFixture fx) => _fx = fx;
        public async ValueTask DisposeAsync() => await SetScopeGateAsync(_fx, enabled: false);
    }
}
