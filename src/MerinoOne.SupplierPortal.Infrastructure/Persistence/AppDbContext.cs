using System.Linq.Expressions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
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
    private readonly AuditableEntityInterceptor _auditInterceptor;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser, AuditableEntityInterceptor auditInterceptor)
        : base(options)
    {
        _currentUser = currentUser;
        _auditInterceptor = auditInterceptor;
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

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(_auditInterceptor);
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

                if (typeof(ISeccode).IsAssignableFrom(clrType))
                {
                    var seccodePredicate = BuildSeccodePredicate(parameter);
                    if (seccodePredicate != null)
                        filter = Expression.AndAlso(notDeleted, seccodePredicate);
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

    // Instance accessors used by the seccode global filter. EF Core evaluates property access
    // on a DbContext-typed Expression.Constant at query time (not at model-build time), so the
    // values below are read PER REQUEST instead of being baked into the cached compiled model.
    public string SecCurrentUserCode => _currentUser?.UserCode ?? string.Empty;
    public bool SecCurrentUserIsPrivileged => _currentUser?.IsAdmin == true || _currentUser?.IsManager == true;
    public bool SecCurrentUserIsAuthenticated => _currentUser?.IsAuthenticated == true;

    private Expression? BuildSeccodePredicate(ParameterExpression parameter)
    {
        // CRITICAL: do not bake _currentUser values into Expression.Constant — the model is built
        // ONCE and cached. Reference DbContext instance properties so EF parameterizes per query.
        var ctx = Expression.Constant(this);
        var privilegedAccess = Expression.Property(ctx, nameof(SecCurrentUserIsPrivileged));
        var authenticatedAccess = Expression.Property(ctx, nameof(SecCurrentUserIsAuthenticated));
        var userCodeAccess = Expression.Property(ctx, nameof(SecCurrentUserCode));

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

        // Combined: privileged sees all rows; otherwise authenticated user filtered by SecRights.
        // Anonymous (not authenticated, not privileged) gets nothing.
        var nonPrivilegedAllowed = Expression.AndAlso(authenticatedAccess, rowMatchesUser);
        return Expression.OrElse(privilegedAccess, nonPrivilegedAllowed);
    }

    public override int SaveChanges() => base.SaveChanges();
    public override Task<int> SaveChangesAsync(CancellationToken ct = default) => base.SaveChangesAsync(ct);
}
