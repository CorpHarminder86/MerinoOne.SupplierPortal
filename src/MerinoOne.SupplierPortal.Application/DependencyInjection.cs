using System.Reflection;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Behaviours;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Application.Users.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace MerinoOne.SupplierPortal.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var asm = Assembly.GetExecutingAssembly();
        services.AddMediatR(c => c.RegisterServicesFromAssembly(asm));
        services.AddValidatorsFromAssembly(asm);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));

        // Shared inbound master-data upsert orchestration (Payment Term / Delivery Term). Scoped so it
        // ctor-injects the per-request IAppDbContext / ICurrentUser / ICurrentCompany.
        services.AddScoped<InboundUpsertExecutor>();
        // Tenant-scoped variant for the reference masters (Currency/Country/State/City/PostalCode) —
        // no company resolution / share-group / anti-spoof; reuses the idempotency + SyncLog + endpoint gate.
        services.AddScoped<TenantInboundUpsertExecutor>();

        // Shared user↔supplier mapping primitives reused by MapSupplier / UnmapSupplier / the bulk
        // SetCompanySupplierMaps command. Scoped — ctor-injects the per-request IAppDbContext.
        services.AddScoped<SupplierMapService>();
        return services;
    }
}
