using System.Linq.Expressions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Comm;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
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
    public DbSet<Tenant> Tenants => Set<Tenant>();

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

    private Expression? BuildSeccodePredicate(ParameterExpression parameter)
    {
        var userCode = _currentUser?.UserCode ?? string.Empty;
        var isPrivileged = _currentUser?.IsAdmin == true || _currentUser?.IsManager == true;

        if (isPrivileged) return Expression.Constant(true);
        if (string.IsNullOrEmpty(userCode)) return Expression.Constant(true);

        var ownerProp = Expression.Property(parameter, nameof(ISeccode.Owner));
        var ownerNotNull = Expression.NotEqual(ownerProp, Expression.Constant(null));
        var secRightsProp = Expression.Property(ownerProp, nameof(Seccode.SecRights));

        var srParam = Expression.Parameter(typeof(SecRight), "r");
        var srUserCode = Expression.Property(srParam, nameof(SecRight.UserCode));
        var srCanRead = Expression.Property(srParam, nameof(SecRight.CanRead));
        var equalsUser = Expression.Equal(srUserCode, Expression.Constant(userCode));
        var canRead = Expression.Equal(srCanRead, Expression.Constant(true));
        var srPredicate = Expression.AndAlso(equalsUser, canRead);
        var srLambda = Expression.Lambda<Func<SecRight, bool>>(srPredicate, srParam);

        var anyMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "Any" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(SecRight));

        var anyCall = Expression.Call(anyMethod, secRightsProp, srLambda);
        return Expression.AndAlso(ownerNotNull, anyCall);
    }

    public override int SaveChanges() => base.SaveChanges();
    public override Task<int> SaveChangesAsync(CancellationToken ct = default) => base.SaveChangesAsync(ct);
}
