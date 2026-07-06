using MerinoOne.SupplierPortal.Domain.Entities.Comm;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class DocumentUploadConfiguration : IEntityTypeConfiguration<DocumentUpload>
{
    public void Configure(EntityTypeBuilder<DocumentUpload> b)
    {
        b.ApplyBaseEntityConvention("DocumentUpload", "doc", "documentUpload");
        b.Property(x => x.OwnerEntityType).HasColumnName("ownerEntityType").HasMaxLength(50).IsRequired();
        b.Property(x => x.OwnerEntityId).HasColumnName("ownerEntityId");
        // R4 (2026-06-26) — §3.6: documentType is now a plain string (was enum-as-string). Column unchanged
        // (nvarchar(50)) so NO migration — only the CLR type changed. IsRequired matches the prior non-null enum.
        b.Property(x => x.DocumentType).HasColumnName("documentType").HasMaxLength(50).IsRequired();
        b.Property(x => x.FileName).HasColumnName("fileName").HasMaxLength(500).IsRequired();
        b.Property(x => x.FileUrl).HasColumnName("fileUrl").HasMaxLength(1000).IsRequired();
        b.Property(x => x.FileSizeKb).HasColumnName("fileSizeKb");
        b.Property(x => x.MimeType).HasColumnName("mimeType").HasMaxLength(100);
        b.Property(x => x.UploadedBy).HasColumnName("uploadedBy").HasMaxLength(100);
        b.Property(x => x.AiValidationStatus).HasColumnName("aiValidationStatus").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AiValidationConfidence).HasColumnName("aiValidationConfidence").HasColumnType("decimal(5,2)");
        b.Property(x => x.AiValidationPayload).HasColumnName("aiValidationPayload").HasColumnType("nvarchar(max)");
        b.Property(x => x.AiValidatedAt).HasColumnName("aiValidatedAt").HasColumnType("datetime2");

        // R8 (2026-07-04) — TSD R8 §3.2 / D-R8-15: IDM sync discriminator + durable handle. camelCase per
        // convention (spec's IDMEntityType casing is overridden by the HasColumnName style).
        b.Property(x => x.IdmEntityType).HasColumnName("idmEntityType").HasMaxLength(100);
        b.Property(x => x.Pid).HasColumnName("pid").HasMaxLength(100);

        b.ToTable(t => t.HasCheckConstraint("CK_DocumentUpload_aiValidationStatus",
            "[aiValidationStatus] IN ('Pending','Valid','Flagged','Skipped')"));

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_DocumentUpload_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.OwnerEntityType, x.OwnerEntityId })
            .HasDatabaseName("IX_DocumentUpload_owner");
    }
}

// R4 (2026-06-26) — TSD R4 Addendum §3.6, Component 5 (Attachment Requirement Governance). Configurable
// attachment-type catalogue (tenant-scoped aggregate). UQ on (tenantId, code) filtered isDeleted=0.
public class AttachmentTypeConfiguration : IEntityTypeConfiguration<AttachmentType>
{
    public void Configure(EntityTypeBuilder<AttachmentType> b)
    {
        b.ApplyBaseEntityConvention("AttachmentType", "doc", "attachmentType");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_AttachmentType_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        // Tenant-wide catalogue: one code per tenant. Filtered isDeleted=0 so a soft-deleted type never blocks
        // re-adding the same code (matches the filtered-unique pattern elsewhere in this codebase).
        b.HasIndex("TenantId", nameof(AttachmentType.Code))
            .HasDatabaseName("UQ_AttachmentType_tenant_code").IsUnique()
            .HasFilter("[isDeleted] = 0");
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_AttachmentType_tenant_company");
    }
}

// R4 (2026-06-26) — TSD R4 Addendum §3.7, Component 5. Reference master of attachment-bearing entities
// (Supplier / Asn / Invoice). Tenant-scoped aggregate. UQ on (tenantId, code) filtered isDeleted=0.
public class AttachmentEntityConfiguration : IEntityTypeConfiguration<AttachmentEntity>
{
    public void Configure(EntityTypeBuilder<AttachmentEntity> b)
    {
        b.ApplyBaseEntityConvention("AttachmentEntity", "doc", "attachmentEntity");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_AttachmentEntity_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex("TenantId", nameof(AttachmentEntity.Code))
            .HasDatabaseName("UQ_AttachmentEntity_tenant_code").IsUnique()
            .HasFilter("[isDeleted] = 0");
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_AttachmentEntity_tenant_company");
    }
}

// R4 (2026-06-26) — TSD R4 Addendum §3.8 + decision D5, Component 5. The per-(entity,type) requirement level,
// TWO-TIER: supplierId NULL = tenant default, non-NULL = supplier override (supplier wins). Tenant-scoped
// aggregate. FKs Restrict. Requirement persisted as the enum name (string). TWO filtered-unique indexes encode
// the two tiers (a partial-NULL composite unique can't enforce both, hence the split).
public class AttachmentRequirementPolicyConfiguration : IEntityTypeConfiguration<AttachmentRequirementPolicy>
{
    public void Configure(EntityTypeBuilder<AttachmentRequirementPolicy> b)
    {
        b.ApplyBaseEntityConvention("AttachmentRequirementPolicy", "doc", "attachmentRequirementPolicy");
        b.Property(x => x.AttachmentEntityId).HasColumnName("attachmentEntityId");
        b.Property(x => x.AttachmentTypeId).HasColumnName("attachmentTypeId");
        b.Property(x => x.SupplierId).HasColumnName("supplierId").HasColumnType("uniqueidentifier");
        b.Property(x => x.Requirement).HasColumnName("requirement").HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(x => x.IsActive).HasColumnName("isActive").HasColumnType("bit").HasDefaultValue(true);

        b.HasOne(x => x.AttachmentEntity).WithMany().HasForeignKey(x => x.AttachmentEntityId)
            .HasConstraintName("FK_AttachmentRequirementPolicy_AttachmentEntity_attachmentEntityId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AttachmentType).WithMany().HasForeignKey(x => x.AttachmentTypeId)
            .HasConstraintName("FK_AttachmentRequirementPolicy_AttachmentType_attachmentTypeId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId)
            .HasConstraintName("FK_AttachmentRequirementPolicy_Supplier_supplierId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_AttachmentRequirementPolicy_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        // D5 tier 1 — one tenant DEFAULT per (entity, type). Filtered supplierId IS NULL (+ isDeleted=0).
        b.HasIndex("TenantId", nameof(AttachmentRequirementPolicy.AttachmentEntityId), nameof(AttachmentRequirementPolicy.AttachmentTypeId))
            .HasDatabaseName("UX_ARP_tenant_default").IsUnique()
            .HasFilter("[supplierId] IS NULL AND [isDeleted] = 0");
        // D5 tier 2 — one supplier OVERRIDE per (supplier, entity, type). Filtered supplierId IS NOT NULL.
        b.HasIndex("TenantId", nameof(AttachmentRequirementPolicy.SupplierId), nameof(AttachmentRequirementPolicy.AttachmentEntityId), nameof(AttachmentRequirementPolicy.AttachmentTypeId))
            .HasDatabaseName("UX_ARP_supplier_override").IsUnique()
            .HasFilter("[supplierId] IS NOT NULL AND [isDeleted] = 0");
        b.HasIndex(x => x.AttachmentEntityId).HasDatabaseName("IX_AttachmentRequirementPolicy_attachmentEntityId");
        b.HasIndex(x => x.AttachmentTypeId).HasDatabaseName("IX_AttachmentRequirementPolicy_attachmentTypeId");
        b.HasIndex(x => x.SupplierId).HasDatabaseName("IX_AttachmentRequirementPolicy_supplierId");
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_AttachmentRequirementPolicy_tenant_company");
    }
}

public class CommunicationMessageConfiguration : IEntityTypeConfiguration<CommunicationMessage>
{
    public void Configure(EntityTypeBuilder<CommunicationMessage> b)
    {
        b.ApplyBaseEntityConvention("CommunicationMessage", "comm", "communicationMessage");
        b.Property(x => x.PurchaseOrderId).HasColumnName("purchaseOrderId");
        b.Property(x => x.ThreadId).HasColumnName("threadId");
        b.Property(x => x.SenderUserId).HasColumnName("senderUserId");
        b.Property(x => x.ReceiverUserId).HasColumnName("receiverUserId");
        b.Property(x => x.MessageBody).HasColumnName("messageBody").HasColumnType("nvarchar(max)");
        b.Property(x => x.AttachmentUrl).HasColumnName("attachmentUrl").HasMaxLength(1000);
        b.Property(x => x.SentAt).HasColumnName("sentAt").HasColumnType("datetime2");
        b.Property(x => x.IsRead).HasColumnName("isRead").HasDefaultValue(false);
        b.Property(x => x.ReadAt).HasColumnName("readAt").HasColumnType("datetime2");
        b.Property(x => x.IsSystemMessage).HasColumnName("isSystemMessage").HasDefaultValue(false);

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_CommunicationMessage_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.ThreadId).HasDatabaseName("IX_CommunicationMessage_threadId");
        b.HasIndex(x => x.SenderUserId).HasDatabaseName("IX_CommunicationMessage_senderUserId");
    }
}

public class InforEndpointMapConfiguration : IEntityTypeConfiguration<InforEndpointMap>
{
    public void Configure(EntityTypeBuilder<InforEndpointMap> b)
    {
        b.ApplyBaseEntityConvention("InforEndpointMap", "integration", "inforEndpointMap");
        b.Property(x => x.EntityName).HasColumnName("entityName").HasMaxLength(100).IsRequired();
        b.Property(x => x.Direction).HasColumnName("direction").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.InforEndpointUrl).HasColumnName("inforEndpointUrl").HasMaxLength(500).IsRequired();
        b.Property(x => x.BodName).HasColumnName("bodName").HasMaxLength(100);
        b.Property(x => x.IsEnabled).HasColumnName("isEnabled").HasDefaultValue(true);
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        // Endpoint "session" liveness telemetry.
        b.Property(x => x.LastReceivedAt).HasColumnName("lastReceivedAt").HasColumnType("datetime2");
        b.Property(x => x.LastStatus).HasColumnName("lastStatus").HasMaxLength(20);
        b.Property(x => x.LastIdempotencyKey).HasColumnName("lastIdempotencyKey").HasMaxLength(100);
        b.Property(x => x.LastMessage).HasColumnName("lastMessage").HasMaxLength(2000);
        b.Property(x => x.ReceivedCount).HasColumnName("receivedCount").HasColumnType("int").HasDefaultValue(0);
    }
}

public class InforSyncLogConfiguration : IEntityTypeConfiguration<InforSyncLog>
{
    public void Configure(EntityTypeBuilder<InforSyncLog> b)
    {
        b.ApplyBaseEntityConvention("InforSyncLog", "integration", "inforSyncLog");
        b.Property(x => x.EntityName).HasColumnName("entityName").HasMaxLength(100).IsRequired();
        b.Property(x => x.Direction).HasColumnName("direction").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PayloadRef).HasColumnName("payloadRef").HasMaxLength(500);
        b.Property(x => x.IdempotencyKey).HasColumnName("idempotencyKey").HasMaxLength(100);
        b.Property(x => x.SyncedAt).HasColumnName("syncedAt").HasColumnType("datetime2");
        b.Property(x => x.ErrorMessage).HasColumnName("errorMessage").HasMaxLength(2000);
        b.Property(x => x.EntityId).HasColumnName("entityId").HasMaxLength(400);
        b.Property(x => x.EntityCount).HasColumnName("entityCount").HasColumnType("int").HasDefaultValue(0);
        b.Property(x => x.PayloadJson).HasColumnName("payloadJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.RetryCount).HasColumnName("retryCount").HasColumnType("int").HasDefaultValue(0);

        b.HasIndex(x => x.IdempotencyKey).HasDatabaseName("IX_InforSyncLog_idempotencyKey");
        b.HasIndex(x => x.SyncedAt).HasDatabaseName("IX_InforSyncLog_syncedAt");
    }
}

public class IntegrationErrorConfiguration : IEntityTypeConfiguration<IntegrationError>
{
    public void Configure(EntityTypeBuilder<IntegrationError> b)
    {
        b.ApplyBaseEntityConvention("IntegrationError", "integration", "integrationError");
        b.Property(x => x.SyncLogId).HasColumnName("syncLogId");
        b.Property(x => x.EntityName).HasColumnName("entityName").HasMaxLength(100);
        b.Property(x => x.ErrorMessage).HasColumnName("errorMessage").HasMaxLength(2000);
        b.Property(x => x.StackTrace).HasColumnName("stackTrace").HasColumnType("nvarchar(max)");
        b.Property(x => x.RetryCount).HasColumnName("retryCount");
        b.Property(x => x.LastRetriedAt).HasColumnName("lastRetriedAt").HasColumnType("datetime2");
        b.Property(x => x.IsResolved).HasColumnName("isResolved").HasDefaultValue(false);
        b.Property(x => x.ResolutionNote).HasColumnName("resolutionNote").HasMaxLength(1000);

        b.HasOne(x => x.SyncLog).WithMany().HasForeignKey(x => x.SyncLogId)
            .HasConstraintName("FK_IntegrationError_SyncLog_SyncLogId").OnDelete(DeleteBehavior.SetNull);
    }
}
