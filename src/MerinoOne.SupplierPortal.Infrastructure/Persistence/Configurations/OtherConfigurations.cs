using MerinoOne.SupplierPortal.Domain.Entities.Comm;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class DocumentUploadConfiguration : IEntityTypeConfiguration<DocumentUpload>
{
    public void Configure(EntityTypeBuilder<DocumentUpload> b)
    {
        b.ApplyBaseEntityConvention("DocumentUpload", "doc", "documentUpload");
        b.Property(x => x.OwnerEntityType).HasColumnName("ownerEntityType").HasMaxLength(50).IsRequired();
        b.Property(x => x.OwnerEntityId).HasColumnName("ownerEntityId");
        b.Property(x => x.DocumentType).HasColumnName("documentType").HasConversion<string>().HasMaxLength(50);
        b.Property(x => x.FileName).HasColumnName("fileName").HasMaxLength(500).IsRequired();
        b.Property(x => x.FileUrl).HasColumnName("fileUrl").HasMaxLength(1000).IsRequired();
        b.Property(x => x.FileSizeKb).HasColumnName("fileSizeKb");
        b.Property(x => x.MimeType).HasColumnName("mimeType").HasMaxLength(100);
        b.Property(x => x.UploadedBy).HasColumnName("uploadedBy").HasMaxLength(100);
        b.Property(x => x.AiValidationStatus).HasColumnName("aiValidationStatus").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AiValidationConfidence).HasColumnName("aiValidationConfidence").HasColumnType("decimal(5,2)");
        b.Property(x => x.AiValidationPayload).HasColumnName("aiValidationPayload").HasColumnType("nvarchar(max)");
        b.Property(x => x.AiValidatedAt).HasColumnName("aiValidatedAt").HasColumnType("datetime2");

        b.ToTable(t => t.HasCheckConstraint("CK_DocumentUpload_aiValidationStatus",
            "[aiValidationStatus] IN ('Pending','Valid','Flagged','Skipped')"));

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_DocumentUpload_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.OwnerEntityType, x.OwnerEntityId })
            .HasDatabaseName("IX_DocumentUpload_owner");
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
