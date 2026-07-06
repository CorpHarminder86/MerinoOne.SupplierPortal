using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

// R8 (2026-07-04) — TSD R8. EF config for the outbound-document-sync (Infor IDM) tables. Two-key convention
// (GUID PK nonclustered + INT Seq clustered UX) via ApplyBaseEntityConvention; camelCase columns; named
// constraints; EF auto-named DEFAULTs (project convention overrides explicit DF_ names).

public class OutboundEndpointConfigConfiguration : IEntityTypeConfiguration<OutboundEndpointConfig>
{
    public void Configure(EntityTypeBuilder<OutboundEndpointConfig> b)
    {
        b.ApplyBaseEntityConvention("OutboundEndpointConfig", "integration", "outboundEndpointConfig");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.TargetSystem).HasColumnName("targetSystem").HasMaxLength(30).IsRequired();
        b.Property(x => x.EndpointKey).HasColumnName("endpointKey").HasMaxLength(80).IsRequired();
        b.Property(x => x.HttpMethod).HasColumnName("httpMethod").HasMaxLength(10).IsRequired();
        b.Property(x => x.RelativePath).HasColumnName("relativePath").HasMaxLength(400).IsRequired();
        b.Property(x => x.StaticHeadersJson).HasColumnName("staticHeadersJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.AckParserKey).HasColumnName("ackParserKey").HasMaxLength(60);
        b.Property(x => x.DefaultAcl).HasColumnName("defaultAcl").HasMaxLength(60);
        b.Property(x => x.EntityName).HasColumnName("entityName").HasMaxLength(60);
        b.Property(x => x.IsEnabled).HasColumnName("isEnabled").HasColumnType("bit").HasDefaultValue(false);

        b.ToTable(t => t.HasCheckConstraint("CK_OutboundEndpointConfig_httpMethod",
            "[httpMethod] IN ('POST','PUT','DELETE')"));

        // One live endpoint row per (tenant, endpointKey). Filtered so a soft-deleted row never blocks re-create.
        b.HasIndex(x => new { x.TenantId, x.EndpointKey })
            .HasDatabaseName("UQ_OutboundEndpointConfig_tenant_endpointKey").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

public class IdmAttachmentTypeConfigConfiguration : IEntityTypeConfiguration<IdmAttachmentTypeConfig>
{
    public void Configure(EntityTypeBuilder<IdmAttachmentTypeConfig> b)
    {
        b.ApplyBaseEntityConvention("IdmAttachmentTypeConfig", "integration", "idmAttachmentTypeConfig");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        // 2026-07-06: ownerEntityType (portal entity: Asn/Invoice/Supplier) stored so idmEntityType can be free text.
        b.Property(x => x.OwnerEntityType).HasColumnName("ownerEntityType").HasMaxLength(40);
        // attachmentType is nvarchar(50) to MATCH doc.AttachmentType.code (DocumentUpload.documentType stores it).
        // NULLABLE (2026-07-06): null = catch-all (every document of ownerEntityType).
        b.Property(x => x.AttachmentType).HasColumnName("attachmentType").HasMaxLength(50);
        b.Property(x => x.IdmEntityType).HasColumnName("idmEntityType").HasMaxLength(40).IsRequired();
        b.Property(x => x.EligibilityGateJson).HasColumnName("eligibilityGateJson").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.CreateMappingExpression).HasColumnName("createMappingExpression").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.MutateMappingExpression).HasColumnName("mutateMappingExpression").HasColumnType("nvarchar(max)");
        b.Property(x => x.CreateMappingSeedHash).HasColumnName("createMappingSeedHash").HasMaxLength(64);
        b.Property(x => x.MutateMappingSeedHash).HasColumnName("mutateMappingSeedHash").HasMaxLength(64);
        b.Property(x => x.IsEnabled).HasColumnName("isEnabled").HasColumnType("bit").HasDefaultValue(false);

        // D7 + 2026-07-06: keyed by (ownerEntityType, attachmentType) — NOT idmEntityType (many types may share one
        // entityType), and now including the portal entity so the SAME attachment-type code can map differently per
        // entity (e.g. "Msme" on an ASN vs on a Supplier). A NULL attachmentType (catch-all) is unique per entity:
        // SQL Server treats NULLs as equal for uniqueness, so exactly one all-types row per (tenant, ownerEntityType).
        b.HasIndex(x => new { x.TenantId, x.OwnerEntityType, x.AttachmentType })
            .HasDatabaseName("UQ_IdmAttachmentTypeConfig_tenant_owner_attachmentType").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

public class IdmDocumentOutboxConfiguration : IEntityTypeConfiguration<IdmDocumentOutbox>
{
    public void Configure(EntityTypeBuilder<IdmDocumentOutbox> b)
    {
        b.ApplyBaseEntityConvention("IdmDocumentOutbox", "integration", "idmDocumentOutbox");
        // seccodeId + tenantId/tenantEntityId + rowVersion mapped by the BaseAggregateRoot block.
        b.Property(x => x.DocumentUploadId).HasColumnName("documentUploadId").IsRequired();
        b.Property(x => x.IdmEntityType).HasColumnName("idmEntityType").HasMaxLength(40).IsRequired();
        b.Property(x => x.OwnerEntityId).HasColumnName("ownerEntityId");
        b.Property(x => x.FileName).HasColumnName("fileName").HasMaxLength(500).IsRequired();
        b.Property(x => x.Operation).HasColumnName("operation").HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(14)
            .HasDefaultValue(IdmOutboxStatus.Blocked).IsRequired();
        b.Property(x => x.CorrelationId).HasColumnName("correlationId").IsRequired();
        b.Property(x => x.ExternalId).HasColumnName("externalId").HasMaxLength(100);
        b.Property(x => x.AttemptCount).HasColumnName("attemptCount").HasColumnType("int").HasDefaultValue(0);
        b.Property(x => x.NextAttemptAt).HasColumnName("nextAttemptAt").HasColumnType("datetime2");
        b.Property(x => x.RequestSnapshotJson).HasColumnName("requestSnapshotJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.ResponseJson).HasColumnName("responseJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.LastError).HasColumnName("lastError").HasColumnType("nvarchar(max)");

        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_IdmDocumentOutbox_operation",
                "[operation] IN ('Create','Update','Delete')");
            t.HasCheckConstraint("CK_IdmDocumentOutbox_status",
                "[status] IN ('Blocked','Pending','InFlight','Success','Failed','Unresolvable')");
        });

        b.HasOne(x => x.DocumentUpload).WithMany().HasForeignKey(x => x.DocumentUploadId)
            .HasConstraintName("FK_IdmDocumentOutbox_DocumentUpload_DocumentUploadId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_IdmDocumentOutbox_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("UQ_IdmDocumentOutbox_correlationId").IsUnique();
        // Dispatcher scan: live non-terminal rows by status + due time; INCLUDE the partition columns.
        b.HasIndex(x => new { x.Status, x.NextAttemptAt })
            .HasDatabaseName("IX_IdmDocumentOutbox_status_nextAttemptAt")
            .IncludeProperties(x => new { x.DocumentUploadId, x.Seq });
        // Per-partition FIFO scan.
        b.HasIndex(x => new { x.DocumentUploadId, x.Seq })
            .HasDatabaseName("IX_IdmDocumentOutbox_documentUploadId_seq");
    }
}
