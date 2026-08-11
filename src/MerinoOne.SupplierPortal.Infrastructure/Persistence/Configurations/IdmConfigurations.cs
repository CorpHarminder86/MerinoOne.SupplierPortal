using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

// R8 (2026-07-04) — TSD R8. EF config for the outbound-document-sync (Infor IDM) outbox. Two-key convention
// (GUID PK nonclustered + INT Seq clustered UX) via ApplyBaseEntityConvention; camelCase columns; named
// constraints; EF auto-named DEFAULTs (project convention overrides explicit DF_ names).
// R10 (migration 0051): the R8 config pair (OutboundEndpointConfig + IdmAttachmentTypeConfig) folded into
// integration.OutboundIntegrationConfig (Document kind) — see OutboundIntegrationConfigConfiguration.
// Only the outbox table stays here.

public class IdmDocumentOutboxConfiguration : IEntityTypeConfiguration<IdmDocumentOutbox>
{
    public void Configure(EntityTypeBuilder<IdmDocumentOutbox> b)
    {
        b.ApplyBaseEntityConvention("IdmDocumentOutbox", "integration", "idmDocumentOutbox");
        // seccodeId + tenantId/tenantEntityId + rowVersion mapped by the BaseAggregateRoot block.
        b.Property(x => x.DocumentUploadId).HasColumnName("documentUploadId").IsRequired();
        b.Property(x => x.IdmEntityType).HasColumnName("idmEntityType").HasMaxLength(100).IsRequired();
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
        // 2026-08-11 (migration 0061) — one live Create and one live Delete per document: the seed scan's in-SQL
        // dedupe made durable, so two dispatcher instances racing the same poll cannot both seed (observed on the
        // test DB: two Create rows 76 ms apart, both dispatched → a duplicate item in IDM). The filter is isDeleted,
        // NOT status: a terminal Failed Create must still block re-seeding (D-R8-23), while a reaped row drops out
        // of the filter so the create→delete→reap→re-seed cycle stays open. Update is deliberately excluded — its
        // rows are inserted inside business commands (a violation would surface as a user-facing failure) and a
        // duplicate Update is an idempotent re-write of the same pid, not a new IDM item.
        b.HasIndex(x => new { x.DocumentUploadId, x.Operation })
            .HasDatabaseName("UQ_IdmDocumentOutbox_documentUploadId_operation_live")
            .IsUnique()
            .HasFilter("[isDeleted] = 0 AND [operation] IN ('Create', 'Delete')");
    }
}
