using System.Reflection;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Behaviours;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Security;
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

        // SecRight.canWrite enforcement for supplier-originated aggregates (bank/license). Scoped.
        services.AddScoped<SupplierWriteGuard>();

        // Deferred-upload rebind for license attachments — re-points staged doc.DocumentUpload rows onto a
        // saved SupplierLicense inside the license command's transaction. Scoped (per-request IAppDbContext).
        services.AddScoped<LicenseAttachmentRebinder>();

        // R4 Module 3 — deferred-upload rebind for ASN attachments (staging -> Asn / AsnAttachment) inside the
        // Create/Update ASN command's transaction. Scoped (per-request IAppDbContext).
        services.AddScoped<AsnAttachmentRebinder>();

        // R4 Module 4 / R6 — single source of truth for the GROUPED draft-invoice generation from an ASN (one
        // Draft per (currency, payment-term) group). Used by the ASN submit path (AsnSubmitExecutor, via Approve)
        // and CreateInvoiceFromAsnCommand (manual/Retry). Scoped.
        services.AddScoped<Invoices.DraftInvoiceFromAsnFactory>();

        // R6 — batch proc.Tax rate resolution (IgnoreQueryFilters + tenant guard, plan D12) shared by the
        // generation, Draft line-edit and Submit rate-freeze paths. Scoped (per-request IAppDbContext).
        services.AddScoped<Invoices.TaxRateResolver>();

        // R5 (TSD R5 Addendum §10.2 / §10.4) — the shared ASN final-submit executor. The over-ship atomic guard
        // lives here now (moved from ASN create); the Approve→Submit path reuses it. Scoped (per-request context).
        services.AddScoped<Shipments.AsnSubmitExecutor>();

        // R4 Module 2 — supplier change-management. Typed per-target appliers (Add/Edit/Delete onto the live
        // supplier rows inside the approve transaction) + the post-commit per-line ERP push (Increment-0 outbox).
        // Scoped — ctor-inject the per-request IAppDbContext / ICurrentUser / IOutboxDispatcher.
        services.AddScoped<Suppliers.ChangeRequests.SupplierChangeApplier>();
        services.AddScoped<Suppliers.ChangeRequests.SupplierChangePushService>();

        // R4 Phase 4 — Attachment Requirement Governance (TSD R4 Addendum §8). The two-tier policy evaluator +
        // the shared submit guard (mandatory-block / warning-confirm / skip-audit) called by the ASN / Invoice /
        // Supplier submit handlers. Scoped — ctor-inject the per-request IAppDbContext / ICurrentUser.
        services.AddScoped<Common.Interfaces.IAttachmentPolicyEvaluator, Documents.AttachmentPolicyEvaluator>();
        services.AddScoped<Documents.AttachmentSubmitGuard>();

        // R5 (TSD R5 Addendum §8.1/§8.2 / Component 4) — the SINGLE shared delivery-schedule upsert point, wired
        // into every PO-becomes-shippable transition (Accept / Acknowledge / AutoAccept ingest / AutoRelease) and
        // the material-Modify re-confirmation path. Idempotent, so calling it from multiple sites is safe. Scoped
        // (per-request IAppDbContext); the caller owns the surrounding SaveChanges.
        services.AddScoped<PurchaseOrders.DeliverySchedules.DeliveryScheduleFactory>();

        // RBAC — shared resolve/validate + collision-free diff writer for role↔permission rows, used by
        // CreateRole and AssignPermissions so the rules live in exactly one place. Scoped (per-request IAppDbContext).
        services.AddScoped<Roles.Common.RolePermissionWriter>();
        return services;
    }
}
