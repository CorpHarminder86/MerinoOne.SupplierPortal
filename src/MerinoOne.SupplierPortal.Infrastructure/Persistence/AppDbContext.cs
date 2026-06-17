using System.Linq.Expressions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings.Scope;
using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using MerinoOne.SupplierPortal.Domain.Entities.Comm;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Inv;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Entities.Settings;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;
using SupplierVerificationEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierVerification;
using SupplierAddressEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierAddress;
using SupplierContactEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierContact;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence;

public class AppDbContext : DbContext, IAppDbContext
{
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentCompany _currentCompany;
    private readonly AuditableEntityInterceptor _auditInterceptor;
    private readonly ScopeStampInterceptor _scopeStampInterceptor;
    private readonly IScopeFilterGate? _scopeFilterGate;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        ICurrentUser currentUser,
        ICurrentCompany currentCompany,
        AuditableEntityInterceptor auditInterceptor,
        ScopeStampInterceptor scopeStampInterceptor,
        IScopeFilterGate? scopeFilterGate = null)
        : base(options)
    {
        _currentUser = currentUser;
        _currentCompany = currentCompany;
        _auditInterceptor = auditInterceptor;
        _scopeStampInterceptor = scopeStampInterceptor;
        _scopeFilterGate = scopeFilterGate;
    }

    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Seccode> Seccodes => Set<Seccode>();
    public DbSet<SecRight> SecRights => Set<SecRight>();
    public DbSet<SupplierUserMap> SupplierUserMaps => Set<SupplierUserMap>();
    public DbSet<SupplierInvite> SupplierInvites => Set<SupplierInvite>();
    public DbSet<InviteOtp> InviteOtps => Set<InviteOtp>();
    public DbSet<LoginOtp> LoginOtps => Set<LoginOtp>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantEntity> TenantEntities => Set<TenantEntity>();
    public DbSet<UserCompanyMap> UserCompanyMaps => Set<UserCompanyMap>();

    public DbSet<Item> Items => Set<Item>();
    public DbSet<DeliveryTerm> DeliveryTerms => Set<DeliveryTerm>();
    public DbSet<PaymentTerm> PaymentTerms => Set<PaymentTerm>();

    public DbSet<SupplierEntity> Suppliers => Set<SupplierEntity>();
    public DbSet<SupplierVerificationEntity> SupplierVerifications => Set<SupplierVerificationEntity>();
    public DbSet<SupplierAddressEntity> SupplierAddresses => Set<SupplierAddressEntity>();
    public DbSet<SupplierContactEntity> SupplierContacts => Set<SupplierContactEntity>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<DeliverySchedule> DeliverySchedules => Set<DeliverySchedule>();
    public DbSet<Asn> Asns => Set<Asn>();
    public DbSet<AsnLine> AsnLines => Set<AsnLine>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<CreditDebitNote> CreditDebitNotes => Set<CreditDebitNote>();
    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<DocumentUpload> DocumentUploads => Set<DocumentUpload>();
    public DbSet<CommunicationMessage> CommunicationMessages => Set<CommunicationMessage>();

    public DbSet<InforEndpointMap> InforEndpointMaps => Set<InforEndpointMap>();
    public DbSet<InforSyncLog> InforSyncLogs => Set<InforSyncLog>();
    public DbSet<IntegrationError> IntegrationErrors => Set<IntegrationError>();
    public DbSet<CompanyShareGroup> CompanyShareGroups => Set<CompanyShareGroup>();
    public DbSet<CompanyShareGroupMember> CompanyShareGroupMembers => Set<CompanyShareGroupMember>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ApiKeyCompany> ApiKeyCompanies => Set<ApiKeyCompany>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(_auditInterceptor, _scopeStampInterceptor);
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplyGlobalFilters(modelBuilder);
    }

    private void ApplyGlobalFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(ISoftDelete).IsAssignableFrom(clrType))
            {
                var parameter = Expression.Parameter(clrType, "e");
                var prop = Expression.Property(parameter, nameof(ISoftDelete.IsDeleted));
                var notDeleted = Expression.Equal(prop, Expression.Constant(false));

                Expression filter = notDeleted;

                // 1. Always-on tenant filter — every ITenantOwned / ITenantScoped / ICompanyScoped type.
                var tenantPredicate = BuildTenantPredicate(clrType, parameter);
                if (tenantPredicate != null)
                    filter = Expression.AndAlso(filter, tenantPredicate);

                // 2/3. Always-on company filter — business data only. Sharing-aware for PaymentTerm/DeliveryTerm
                //      (ICompanyScoped), plain TenantEntityId == ActiveCompanyId for ITenantScoped aggregates.
                var companyPredicate = BuildCompanyPredicate(clrType, parameter);
                if (companyPredicate != null)
                    filter = Expression.AndAlso(filter, companyPredicate);

                // 4. Seccode RLS (unchanged) — ANDed for ISeccode aggregates.
                if (typeof(ISeccode).IsAssignableFrom(clrType))
                {
                    var seccodePredicate = BuildSeccodePredicate(parameter);
                    if (seccodePredicate != null)
                        filter = Expression.AndAlso(filter, seccodePredicate);
                }

                var lambda = Expression.Lambda(filter, parameter);
                modelBuilder.Entity(clrType).HasQueryFilter(lambda);
            }

            if (typeof(IHasRowVersion).IsAssignableFrom(clrType))
            {
                modelBuilder.Entity(clrType)
                    .Property(nameof(IHasRowVersion.RowVersion))
                    .IsRowVersion()
                    .HasColumnName("rowVersion");
            }
        }
    }

    // === Always-on scope filter instance members ====================================================
    // CRITICAL: like the seccode members, these are read PER QUERY (EF evaluates property access on a
    // DbContext-typed Expression.Constant), never baked into the cached compiled model.

    // === Feature-flag rollout gate (Scope.FiltersEnabled) ===========================================
    // The two always-on scope filters (tenant + company) are gated by a global SystemSetting so they can be
    // flipped ON only AFTER the scope backfill has stamped every legacy row — preventing a "dark portal"
    // where NULL-scope rows are invisible during the backfill window. The value is read from the singleton
    // IScopeFilterGate (which loads + caches it via its OWN scope) — NEVER via this request context — so the
    // per-query predicate never triggers a nested DB read / "second operation" error during query evaluation.
    // Fail-OPEN (filters bypassed) when the gate is absent (design-time/tests) or off; the seccode RLS filter
    // is NEVER gated.

    /// <summary>
    /// True only when the <c>Scope.FiltersEnabled</c> SystemSetting is "true". When false (the default, and
    /// the state during the backfill window) the tenant + company predicates are bypassed. Reads the cached
    /// singleton gate — zero DB I/O on this context.
    /// </summary>
    public bool ScopeFiltersEnabled => _scopeFilterGate?.FiltersEnabled ?? false;

    /// <summary>Platform Admin (cross-tenant) and the system principal bypass the tenant filter.</summary>
    public bool TenantFilterBypassed => _currentUser?.IsPlatformAdmin == true || _currentUser is ISystemPrincipal;

    public Guid? CurrentTenantId => _currentUser?.TenantId;

    /// <summary>ONLY the system principal bypasses the company filter — no role-based bypass (even Tenant Admin).</summary>
    public bool CompanyFilterBypassed => _currentCompany is ISystemCompany;

    public Guid? ActiveCompanyId => _currentCompany?.ActiveCompanyId;

    public Guid? PaymentTermSourceCompanyId =>
        _currentCompany?.ResolveSource(Domain.Enums.SharedEndpoint.PaymentTerm, _currentCompany.ActiveCompanyId);

    public Guid? DeliveryTermSourceCompanyId =>
        _currentCompany?.ResolveSource(Domain.Enums.SharedEndpoint.DeliveryTerm, _currentCompany.ActiveCompanyId);

    /// <summary>
    /// tenantFilterBypassed OR (e.TenantId != null AND e.TenantId == CurrentTenantId).
    /// Applies to every type carrying a TenantId (ITenantOwned / ITenantScoped / ICompanyScoped).
    /// </summary>
    private Expression? BuildTenantPredicate(Type clrType, ParameterExpression parameter)
    {
        var carriesTenant = typeof(ITenantOwned).IsAssignableFrom(clrType)
                            || typeof(ITenantScoped).IsAssignableFrom(clrType)
                            || typeof(ICompanyScoped).IsAssignableFrom(clrType);
        if (!carriesTenant) return null;

        var ctx = Expression.Constant(this);
        var bypass = Expression.Property(ctx, nameof(TenantFilterBypassed));
        var currentTenant = Expression.Property(ctx, nameof(CurrentTenantId));

        var tenantProp = Expression.Property(parameter, "TenantId");          // Guid? on all three markers
        var tenantNotNull = Expression.NotEqual(tenantProp, Expression.Constant(null, typeof(Guid?)));
        var tenantEquals = Expression.Equal(tenantProp, currentTenant);
        var matches = Expression.AndAlso(tenantNotNull, tenantEquals);

        // Feature-flag rollout gate: when Scope.FiltersEnabled is off (backfill window / not yet flipped),
        // the tenant filter is bypassed so NULL-scope rows remain visible (no dark portal). Read per query.
        var filtersDisabled = Expression.Not(Expression.Property(ctx, nameof(ScopeFiltersEnabled)));

        return Expression.OrElse(filtersDisabled, Expression.OrElse(bypass, matches));
    }

    /// <summary>
    /// Company filter for business data:
    ///   - PaymentTerm/DeliveryTerm (ICompanyScoped) → sharing-aware: TenantEntityId == X{...}SourceCompanyId.
    ///   - ITenantScoped aggregates (incl. Supplier) → plain: TenantEntityId == ActiveCompanyId.
    /// companyFilterBypassed OR (e.TenantEntityId != null AND e.TenantEntityId == source).
    /// </summary>
    private Expression? BuildCompanyPredicate(Type clrType, ParameterExpression parameter)
    {
        string sourceMember;
        if (clrType == typeof(PaymentTerm))
            sourceMember = nameof(PaymentTermSourceCompanyId);
        else if (clrType == typeof(DeliveryTerm))
            sourceMember = nameof(DeliveryTermSourceCompanyId);
        else if (typeof(ITenantScoped).IsAssignableFrom(clrType))
            sourceMember = nameof(ActiveCompanyId);
        else
            return null;   // ITenantOwned config / integration entities are NOT company-scoped.

        var ctx = Expression.Constant(this);
        var bypass = Expression.Property(ctx, nameof(CompanyFilterBypassed));
        var source = Expression.Property(ctx, sourceMember);

        var companyProp = Expression.Property(parameter, "TenantEntityId");   // Guid? on the relevant markers
        var companyNotNull = Expression.NotEqual(companyProp, Expression.Constant(null, typeof(Guid?)));
        var companyEquals = Expression.Equal(companyProp, source);
        var matches = Expression.AndAlso(companyNotNull, companyEquals);

        // Same rollout gate as the tenant predicate — bypass the company filter until Scope.FiltersEnabled is on.
        var filtersDisabled = Expression.Not(Expression.Property(ctx, nameof(ScopeFiltersEnabled)));

        return Expression.OrElse(filtersDisabled, Expression.OrElse(bypass, matches));
    }

    // Instance accessors used by the seccode global filter. EF Core evaluates property access
    // on a DbContext-typed Expression.Constant at query time (not at model-build time), so the
    // values below are read PER REQUEST instead of being baked into the cached compiled model.
    public string SecCurrentUserCode => _currentUser?.UserCode ?? string.Empty;
    public bool SecCurrentUserIsPrivileged => _currentUser?.IsAdmin == true || _currentUser?.IsManager == true;
    public bool SecCurrentUserIsAuthenticated => _currentUser?.IsAuthenticated == true;

    /// <summary>
    /// Direct full-company grant: the active company is one this user has an <c>AllSuppliers=true</c>
    /// UserCompanyMap for. ORed into the seccode predicate so the user sees EVERY supplier in the active
    /// company. SAFE because the company filter already ANDs <c>TenantEntityId == ActiveCompanyId</c>, so
    /// the bypass is scoped to the active company — no cross-company leak. Resolved from the DB per request
    /// (never a JWT claim) by <see cref="ICurrentCompany.ActiveCompanyFullAccess"/>.
    /// </summary>
    public bool SecActiveCompanyFullAccess => _currentCompany?.ActiveCompanyFullAccess == true;

    private Expression? BuildSeccodePredicate(ParameterExpression parameter)
    {
        // CRITICAL: do not bake _currentUser values into Expression.Constant — the model is built
        // ONCE and cached. Reference DbContext instance properties so EF parameterizes per query.
        var ctx = Expression.Constant(this);
        var privilegedAccess = Expression.Property(ctx, nameof(SecCurrentUserIsPrivileged));
        var authenticatedAccess = Expression.Property(ctx, nameof(SecCurrentUserIsAuthenticated));
        var userCodeAccess = Expression.Property(ctx, nameof(SecCurrentUserCode));
        var fullAccess = Expression.Property(ctx, nameof(SecActiveCompanyFullAccess));

        var ownerProp = Expression.Property(parameter, nameof(ISeccode.Owner));
        var ownerNotNull = Expression.NotEqual(ownerProp, Expression.Constant(null));
        var secRightsProp = Expression.Property(ownerProp, nameof(Seccode.SecRights));

        var srParam = Expression.Parameter(typeof(SecRight), "r");
        var srUserCode = Expression.Property(srParam, nameof(SecRight.UserCode));
        var srCanRead = Expression.Property(srParam, nameof(SecRight.CanRead));
        var equalsUser = Expression.Equal(srUserCode, userCodeAccess);
        var canRead = Expression.Equal(srCanRead, Expression.Constant(true));
        var srPredicate = Expression.AndAlso(equalsUser, canRead);
        var srLambda = Expression.Lambda<Func<SecRight, bool>>(srPredicate, srParam);

        var anyMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "Any" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(SecRight));
        var anyCall = Expression.Call(anyMethod, secRightsProp, srLambda);

        // Per-row check: row owner is set AND a SecRight exists for current user.
        var rowMatchesUser = Expression.AndAlso(ownerNotNull, anyCall);

        // Combined: privileged sees all rows; a direct full-company grant (SecActiveCompanyFullAccess) sees
        // every supplier in the ACTIVE company (the company filter still ANDs TenantEntityId == ActiveCompanyId,
        // so the bypass can never leak across companies); otherwise an authenticated user is filtered by SecRights.
        // Anonymous (not authenticated, not privileged, no grant) gets nothing.
        var nonPrivilegedAllowed = Expression.AndAlso(authenticatedAccess, rowMatchesUser);
        return Expression.OrElse(privilegedAccess, Expression.OrElse(fullAccess, nonPrivilegedAllowed));
    }

    public override int SaveChanges() => base.SaveChanges();
    public override Task<int> SaveChangesAsync(CancellationToken ct = default) => base.SaveChangesAsync(ct);

    /// <summary>
    /// Explicit relational transaction for the inbound integration upsert path. Exposed via
    /// <see cref="IAppDbContext"/> so Application-layer handlers get transactional access without an
    /// Infrastructure reference.
    /// </summary>
    public Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Database.BeginTransactionAsync(ct);

    public void ClearChangeTracker() => ChangeTracker.Clear();
}
