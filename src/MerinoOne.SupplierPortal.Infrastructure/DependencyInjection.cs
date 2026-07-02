using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Application.SystemSettings.Invoicing;
using MerinoOne.SupplierPortal.Application.SystemSettings.Registry;
using MerinoOne.SupplierPortal.Application.SystemSettings.Scope;
using MerinoOne.SupplierPortal.Application.SystemSettings.SupplierInvite;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.StatusMapping;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Interceptors;
using MerinoOne.SupplierPortal.Infrastructure.Security;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration cfg)
    {
        // SECURITY: the connection string is intentionally NOT committed (no plaintext SA secret in appsettings.json).
        // Dev resolves it from user-secrets (auto-loaded in Development); prod from the
        // ConnectionStrings__DefaultConnection environment variable. A blank/whitespace value fails fast here.
        var cs = cfg.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured. Set it via user-secrets (dev) or the " +
                "ConnectionStrings__DefaultConnection environment variable (prod).");

        services.AddScoped<AuditableEntityInterceptor>();
        // Stamps tenant + active-company onto inserted rows (skips the system principal). Scoped so it
        // ctor-injects the per-request ICurrentUser / ICurrentCompany.
        services.AddScoped<ScopeStampInterceptor>();

        services.AddDbContext<AppDbContext>((sp, opts) =>
        {
            opts.UseSqlServer(cs, sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name));
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // Dapper-backed raw SQL factory (global search, audit trail)
        services.AddSingleton<ISqlConnectionFactory>(_ => new SqlConnectionFactory(cs));

        // Mock integration services (Stage 1)
        services.AddScoped<INicValidationService, MockNicValidationService>();
        services.AddScoped<IDocumentValidationService, MockDocumentValidationService>();

        // ── Infor CloudSuite (ION API) ────────────────────────────────────────────────────────
        // Shared, stateless OAuth2 token client — used by both the Settings "Test connection" path
        // and the live outbound integration's token provider.
        services.AddSingleton<InforOAuthTokenClient>();
        services.AddScoped<IInforConnectionTester, InforConnectionTester>();
        // Per-tenant connection reader (decrypts secrets) + cached token provider.
        services.AddScoped<IInforConnectionProvider, InforConnectionProvider>();
        services.AddScoped<IInforTokenProvider, InforTokenProvider>();

        // Outbound integration implementation — swap mock↔live via Integration:Mode (mirrors Email:Mode).
        // Default "Mock" so existing behaviour is unchanged until an operator opts in to "Live".
        var inforMode = cfg["Integration:Mode"] ?? "Mock";
        if (inforMode.Equals("Live", StringComparison.OrdinalIgnoreCase))
            services.AddScoped<IInforIntegrationService, LiveInforIntegrationService>();
        else
            services.AddScoped<IInforIntegrationService, MockInforIntegrationService>();

        // ── Outbound transactional outbox (Increment 0) ────────────────────────────────────────
        // EnqueueAsync adds a Pending OutboxMessage in the caller's unit of work; the post-commit
        // OutboxDispatcherWorker drains it (no ERP HTTP call ever inside a DB txn — fixes D1/D2/D3).
        services.AddScoped<IOutboxDispatcher, Integration.Outbox.OutboxDispatcher>();
        // Scoped so the dispatcher (and RetryIntegrationErrorCommand) can thread the deterministic key into the
        // Live service without changing the IInforIntegrationService interface. Replayed as the ERP idempotency key.
        services.AddScoped<IOutboundIdempotencyContext, Integration.Outbox.OutboundIdempotencyContext>();
        services.AddHostedService<Integration.Outbox.OutboxDispatcherWorker>();

        // File storage (Mock-then-Live). Stage 1 = local disk under {ContentRoot}/uploads.
        // Stage 2 will swap to Azure Blob behind the same IFileStorageService interface.
        services.AddSingleton<IFileStorageService, LocalDiskFileStorageService>();

        // R6 (plan D13) — invoice PDF rendering (QuestPDF). The Community license is asserted ONCE here (both
        // hosts call AddInfrastructure). NOTE: Community licensing is revenue-gated (<$1M USD/yr) — flagged to
        // the owner in the build plan; WeasyPrint is the documented fallback if the terms stop fitting.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        services.AddSingleton<IInvoicePdfGenerator, Services.InvoiceQuestPdfGenerator>();

        // In-process memory cache — backs the IEmailTemplateRenderer 60s lookup. Safe to call
        // twice; Microsoft.Extensions registers a single MemoryCache + IMemoryCache pair.
        services.AddMemoryCache();

        // Distributed cache backs the effective-permission + active-status resolvers (short 60s TTL +
        // explicit invalidate-on-write). This single-node in-memory impl is correct for one instance;
        // for a multi-instance deployment swap to AddStackExchangeRedisCache so an invalidation on one
        // node evicts every node (a per-node cache would otherwise serve stale perms up to the TTL).
        services.AddDistributedMemoryCache();
        services.AddScoped<IEffectivePermissionService, EffectivePermissionService>();
        services.AddScoped<IUserStatusService, UserStatusService>();

        // Admin-editable email template renderer (used by the TemplateAwareEmailService
        // decorator below, and by the test-send command in the EmailTemplates admin endpoints).
        services.AddScoped<IEmailTemplateRenderer, EmailTemplateRenderer>();

        // Email transport — split into two contracts:
        //   IEmailService  → public, used by handlers; goes to TemplateAwareEmailService which
        //                    renders the template and ENQUEUES a row in admin.emailOutbox. Fast,
        //                    never touches SMTP, never blocks the API response.
        //   IEmailSender   → low-level raw SMTP/Mock; resolved by EmailOutboxWorker only.
        // Email:Mode = "Mock" (dev) | "Live" (default). The Mock impl writes to logs/emails-*.log
        // so QA can fish OTPs out without an SMTP server.
        var emailMode = cfg["Email:Mode"] ?? "Live";
        if (emailMode.Equals("Mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<MockEmailService>();
            services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<MockEmailService>());
        }
        else
        {
            services.AddScoped<SmtpEmailService>();
            services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<SmtpEmailService>());
        }
        // Single public IEmailService — always the enqueueing decorator. Worker uses IEmailSender
        // directly so it never routes through the enqueue path (no infinite loop).
        services.AddScoped<IEmailService, TemplateAwareEmailService>();

        // Async drain. Hosted service so it starts with the host and stops cleanly on shutdown.
        services.AddHostedService<EmailOutboxWorker>();

        // R4 (2026-06-26) — Phase 6 / UC-PO-04: 48h negotiation-SLA buyer nudge. BackgroundService (mirrors
        // EmailOutboxWorker) — polls Fulfilment:SlaNudgeIntervalMinutes (default 30), finds Submitted negotiations
        // older than Fulfilment:SlaNudgeHours (default 48) with NudgeSentAt null, enqueues a buyer reminder + stamps
        // NudgeSentAt to dedupe.
        services.AddHostedService<SlaNudgeWorker>();

        // Cryptographically-strong numeric OTP generator (invite + MFA flows).
        services.AddSingleton<IOtpCodeGenerator, OtpCodeGenerator>();

        // Password hashing — adapter over the static PasswordHasher (Application has no Infra ref).
        services.AddSingleton<IPasswordHasher, PasswordHasherService>();

        // API-key hashing — adapter over the static ApiKeyHasher (SHA-256 + FixedTimeEquals).
        services.AddSingleton<IApiKeyHasher, ApiKeyHasherService>();

        // System settings — generic shell, per-category seeds, password protection, cached readers.
        // Data Protection — PERSIST the key ring to a stable folder + fixed app name. Without this the default
        // key ring lives in the app-pool profile and is regenerated on every publish / app-pool recycle, which
        // silently makes every previously-encrypted value undecryptable after a deploy (protected SMTP/settings
        // here; the Blazor Web host's ProtectedLocalStorage JWT separately). Path is configurable
        // (DataProtection:KeysPath); when unset (design-time tools / tests) it falls back to the default ring.
        var dp = services.AddDataProtection().SetApplicationName("MerinoOne.SupplierPortal");
        var dpKeysPath = cfg["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(dpKeysPath))
        {
            System.IO.Directory.CreateDirectory(dpKeysPath);
            dp.PersistKeysToFileSystem(new System.IO.DirectoryInfo(dpKeysPath));
        }
        // Singleton — DataProtectorSettingProtector wraps the singleton IDataProtectionProvider
        // and is consumed from singleton EmailConfigService and scoped MediatR handlers alike.
        services.AddSingleton<ISettingProtector, DataProtectorSettingProtector>();

        // Seeds: registered concretely so services can ctor-inject the typed seed and also as
        // ISettingsCategorySeed so SettingsSeedRegistry enumerates them.
        services.AddSingleton<EmailConfigSeed>();
        services.AddSingleton<SupplierInviteSeed>();
        services.AddSingleton<FulfilmentSeed>();
        services.AddSingleton<InvoicingSeed>();
        services.AddSingleton<ISettingsCategorySeed>(sp => sp.GetRequiredService<EmailConfigSeed>());
        services.AddSingleton<ISettingsCategorySeed>(sp => sp.GetRequiredService<SupplierInviteSeed>());
        services.AddSingleton<ISettingsCategorySeed>(sp => sp.GetRequiredService<FulfilmentSeed>());
        services.AddSingleton<ISettingsCategorySeed>(sp => sp.GetRequiredService<InvoicingSeed>());
        services.AddSingleton<SettingsSeedRegistry>();

        // Cached readers — singleton so the cache persists across requests; expose both the
        // typed reader and ISettingsCacheInvalidator so Save/Reset handlers fan invalidations out.
        services.AddSingleton<EmailConfigService>();
        services.AddSingleton<IEmailConfig>(sp => sp.GetRequiredService<EmailConfigService>());
        services.AddSingleton<ISettingsCacheInvalidator>(sp => sp.GetRequiredService<EmailConfigService>());

        services.AddSingleton<SupplierInviteSettingsService>();
        services.AddSingleton<ISupplierInviteSettings>(sp => sp.GetRequiredService<SupplierInviteSettingsService>());
        services.AddSingleton<ISettingsCacheInvalidator>(sp => sp.GetRequiredService<SupplierInviteSettingsService>());

        // R4 (2026-06-26) — Decision D3: the over-ship-guard rollout flag (Fulfilment.EnforceOverShipGuard).
        services.AddSingleton<FulfilmentSettingsService>();
        services.AddSingleton<IFulfilmentSettings>(sp => sp.GetRequiredService<FulfilmentSettingsService>());
        services.AddSingleton<ISettingsCacheInvalidator>(sp => sp.GetRequiredService<FulfilmentSettingsService>());

        // R6 (plan D11) — invoice-submit e-invoice compliance gates (Invoicing.RequireIrn / RequireEWayBill).
        services.AddSingleton<InvoicingSettingsService>();
        services.AddSingleton<IInvoicingSettings>(sp => sp.GetRequiredService<InvoicingSettingsService>());
        services.AddSingleton<ISettingsCacheInvalidator>(sp => sp.GetRequiredService<InvoicingSettingsService>());

        // R5 (TSD R5 Addendum §11 / Component 7) — cached ERP→portal PO-status map (per tenant). Singleton so the
        // map persists across requests with zero DB I/O on the inbound hot path; invalidated like the other cached
        // readers when a mapping row is saved/deleted.
        services.AddSingleton<PoStatusMapService>();
        services.AddSingleton<IPoStatusMap>(sp => sp.GetRequiredService<PoStatusMapService>());
        services.AddSingleton<ISettingsCacheInvalidator>(sp => sp.GetRequiredService<PoStatusMapService>());

        // Scope-filter rollout gate — singleton so the request DbContext reads the flag with zero DB I/O
        // (no re-entrancy during query-filter evaluation). Invalidated like the other cached readers.
        services.AddSingleton<ScopeFilterGate>();
        services.AddSingleton<IScopeFilterGate>(sp => sp.GetRequiredService<ScopeFilterGate>());
        services.AddSingleton<ISettingsCacheInvalidator>(sp => sp.GetRequiredService<ScopeFilterGate>());

        // ef tooling / migrations / seed contexts get the anonymous (system) user + company.
        services.TryAddCurrentUserFallback();
        services.TryAddCurrentCompanyFallback();

        return services;
    }

    private static IServiceCollection TryAddCurrentUserFallback(this IServiceCollection services)
    {
        // host project (API/Web) will register the real HttpContext-backed implementation;
        // here we provide a fallback so design-time tools (dotnet ef) and tests don't crash.
        if (!services.Any(d => d.ServiceType == typeof(ICurrentUser)))
        {
            services.AddScoped<ICurrentUser, AnonymousCurrentUser>();
        }
        return services;
    }

    private static IServiceCollection TryAddCurrentCompanyFallback(this IServiceCollection services)
    {
        // Same pattern as ICurrentUser: the API host registers HttpContextCurrentCompany; design-time
        // tooling, seeders and workers fall back to the system company (bypasses the company filter).
        if (!services.Any(d => d.ServiceType == typeof(ICurrentCompany)))
        {
            services.AddScoped<ICurrentCompany, AnonymousCurrentCompany>();
        }
        return services;
    }
}
