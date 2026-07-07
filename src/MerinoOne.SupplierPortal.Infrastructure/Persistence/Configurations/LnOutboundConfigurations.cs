using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

/// <summary>
/// R10 (migration 0051; born as the R9 LN endpoint config, migration 0047) —
/// <c>integration.OutboundIntegrationConfig</c>: the unified outbound-integration config plane. One row =
/// one integration (connection + gate + request/response/ack mappings + portal entity); <c>kind</c> selects
/// the executor (Transaction → OutboxMessage pipeline, Document → IdmDocumentOutbox pipeline). Two-key
/// pattern + audit block via <see cref="BaseEntityConfigExtensions.ApplyBaseEntityConvention"/>.
/// </summary>
public class OutboundIntegrationConfigConfiguration : IEntityTypeConfiguration<OutboundIntegrationConfig>
{
    public void Configure(EntityTypeBuilder<OutboundIntegrationConfig> b)
    {
        b.ApplyBaseEntityConvention("OutboundIntegrationConfig", "integration", "outboundIntegrationConfig");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(OutboundIntegrationKind.Transaction).IsRequired();
        b.Property(x => x.ConnectionPointId).HasColumnName("connectionPointId");
        b.Property(x => x.TransactionType).HasColumnName("transactionType").HasMaxLength(60);
        b.Property(x => x.PortalEntity).HasColumnName("portalEntity").HasMaxLength(60).IsRequired();
        // attachmentType matches doc.AttachmentType.code width; targetEntityName matches the old idmEntityType width.
        b.Property(x => x.AttachmentType).HasColumnName("attachmentType").HasMaxLength(50);
        b.Property(x => x.TargetEntityName).HasColumnName("targetEntityName").HasMaxLength(100);
        b.Property(x => x.ContextJson).HasColumnName("contextJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.EndpointPath).HasColumnName("endpointPath").HasMaxLength(400).IsRequired();
        b.Property(x => x.HttpVerb).HasColumnName("httpVerb").HasMaxLength(10).HasDefaultValue("POST").IsRequired();
        b.Property(x => x.MutatePath).HasColumnName("mutatePath").HasMaxLength(400);
        b.Property(x => x.MutateVerb).HasColumnName("mutateVerb").HasMaxLength(10);
        b.Property(x => x.DeletePath).HasColumnName("deletePath").HasMaxLength(400);
        b.Property(x => x.DeleteVerb).HasColumnName("deleteVerb").HasMaxLength(10);
        b.Property(x => x.StaticHeadersJson).HasColumnName("staticHeadersJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.RequestFormat).HasColumnName("requestFormat").HasMaxLength(10).HasDefaultValue("Json").IsRequired();
        b.Property(x => x.ResponseFormat).HasColumnName("responseFormat").HasMaxLength(10).HasDefaultValue("Json").IsRequired();
        // Tri-state cutover/kill (D-R9-2 + D-R9-11): CHECK-constrained enum name — Legacy must be the safe
        // default at row creation (Transaction kind; Document rows are save-guarded to Dynamic|Held).
        b.Property(x => x.DispatchMode).HasColumnName("dispatchMode").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(OutboundDispatchMode.Legacy).IsRequired();
        b.Property(x => x.EligibilityGateExpr).HasColumnName("eligibilityGateExpr").HasColumnType("nvarchar(max)");
        b.Property(x => x.RequestMappingExpr).HasColumnName("requestMappingExpr").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.MutateMappingExpr).HasColumnName("mutateMappingExpr").HasColumnType("nvarchar(max)");
        b.Property(x => x.ResponseMappingExpr).HasColumnName("responseMappingExpr").HasColumnType("nvarchar(max)");
        b.Property(x => x.AckMappingExpr).HasColumnName("ackMappingExpr").HasColumnType("nvarchar(max)");
        b.Property(x => x.RequestMappingSeedHash).HasColumnName("requestMappingSeedHash").HasMaxLength(64);
        b.Property(x => x.MutateMappingSeedHash).HasColumnName("mutateMappingSeedHash").HasMaxLength(64);
        b.Property(x => x.ResponseMappingSeedHash).HasColumnName("responseMappingSeedHash").HasMaxLength(64);
        b.Property(x => x.AckMappingSeedHash).HasColumnName("ackMappingSeedHash").HasMaxLength(64);
        b.Property(x => x.CandidateFilterName).HasColumnName("candidateFilterName").HasMaxLength(120);
        b.Property(x => x.CandidateFilterParams).HasColumnName("candidateFilterParams").HasColumnType("nvarchar(max)");
        b.Property(x => x.GateVersion).HasColumnName("gateVersion").HasColumnType("int").HasDefaultValue(1).IsRequired();
        b.Property(x => x.SampleDocumentJson).HasColumnName("sampleDocumentJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.SampleBuilderVersion).HasColumnName("sampleBuilderVersion").HasMaxLength(40);
        b.Property(x => x.ResponseSampleJson).HasColumnName("responseSampleJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.AckSampleJson).HasColumnName("ackSampleJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.VerifiedBy).HasColumnName("verifiedBy").HasMaxLength(100);
        b.Property(x => x.VerifiedAt).HasColumnName("verifiedAt").HasColumnType("datetime2");
        b.Property(x => x.VerifiedNote).HasColumnName("verifiedNote").HasMaxLength(500);
        b.Property(x => x.PathConfirmed).HasColumnName("pathConfirmed").HasColumnType("bit").HasDefaultValue(false).IsRequired();

        // Value guards (config table — hostile values here mean wrong HTTP calls, so DB CHECKs back the C# enums).
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_OutboundIntegrationConfig_kind", "[kind] IN ('Transaction','Document')");
            t.HasCheckConstraint("CK_OutboundIntegrationConfig_httpVerb", "[httpVerb] IN ('POST','PUT','PATCH')");
            t.HasCheckConstraint("CK_OutboundIntegrationConfig_mutateVerb", "[mutateVerb] IS NULL OR [mutateVerb] IN ('POST','PUT','PATCH')");
            t.HasCheckConstraint("CK_OutboundIntegrationConfig_deleteVerb", "[deleteVerb] IS NULL OR [deleteVerb] IN ('POST','PUT','PATCH','DELETE')");
            t.HasCheckConstraint("CK_OutboundIntegrationConfig_dispatchMode", "[dispatchMode] IN ('Legacy','Dynamic','Held')");
            t.HasCheckConstraint("CK_OutboundIntegrationConfig_requestFormat", "[requestFormat] IN ('Json','Xml')");
            t.HasCheckConstraint("CK_OutboundIntegrationConfig_responseFormat", "[responseFormat] IN ('Json','Xml')");
        });

        b.HasOne(x => x.ConnectionPoint).WithMany().HasForeignKey(x => x.ConnectionPointId)
            .HasConstraintName("FK_OutboundIntegrationConfig_ConnectionPoint_ConnectionPointId")
            .OnDelete(DeleteBehavior.Restrict);

        // Per-kind identity, both filtered so a soft-deleted row never blocks re-creation:
        // Transaction — one live config per (tenant, transactionType);
        // Document — one live config per (tenant, portalEntity, attachmentType); NULLs compare equal in a
        // unique index, so exactly one catch-all (NULL attachmentType) row per (tenant, portalEntity).
        b.HasIndex(x => new { x.TenantId, x.TransactionType })
            .HasDatabaseName("UQ_OutboundIntegrationConfig_tenant_transactionType").IsUnique()
            .HasFilter("[isDeleted] = 0 AND [kind] = 'Transaction'");
        b.HasIndex(x => new { x.TenantId, x.PortalEntity, x.AttachmentType })
            .HasDatabaseName("UQ_OutboundIntegrationConfig_tenant_entity_attachmentType").IsUnique()
            .HasFilter("[isDeleted] = 0 AND [kind] = 'Document'");
    }
}

/// <summary>
/// R10 (migration 0051) — <c>integration.ConnectionPoint</c>: named outbound connection target. Exactly one
/// live default per tenant (filtered unique). InforION rows carry no URL/auth (resolved from the tenant's
/// Infor connection settings); other system types own theirs here.
/// </summary>
public class ConnectionPointConfiguration : IEntityTypeConfiguration<ConnectionPoint>
{
    public void Configure(EntityTypeBuilder<ConnectionPoint> b)
    {
        b.ApplyBaseEntityConvention("ConnectionPoint", "integration", "connectionPoint");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        b.Property(x => x.SystemType).HasColumnName("systemType").HasMaxLength(20).IsRequired();
        b.Property(x => x.BaseUrl).HasColumnName("baseUrl").HasMaxLength(400);
        b.Property(x => x.AuthConfigJson).HasColumnName("authConfigJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.IsDefault).HasColumnName("isDefault").HasColumnType("bit").HasDefaultValue(false).IsRequired();
        b.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(500);

        b.ToTable(t => t.HasCheckConstraint("CK_ConnectionPoint_systemType",
            "[systemType] IN ('InforION','Tally','ClearTax','GenericRest')"));

        b.HasIndex(x => new { x.TenantId, x.Name })
            .HasDatabaseName("UQ_ConnectionPoint_tenant_name").IsUnique()
            .HasFilter("[isDeleted] = 0");
        // Exactly one live default per tenant.
        b.HasIndex(x => x.TenantId)
            .HasDatabaseName("UQ_ConnectionPoint_tenant_default").IsUnique()
            .HasFilter("[isDefault] = 1 AND [isDeleted] = 0");
    }
}

/// <summary>R9 (§2.6, migration 0048) — DB-backed kill switch: one row per (tenant, scope); absent row = enabled.</summary>
public class IntegrationSwitchConfiguration : IEntityTypeConfiguration<IntegrationSwitch>
{
    public void Configure(EntityTypeBuilder<IntegrationSwitch> b)
    {
        b.ApplyBaseEntityConvention("IntegrationSwitch", "integration", "integrationSwitch");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(20).IsRequired();
        b.Property(x => x.IsEnabled).HasColumnName("isEnabled").HasColumnType("bit").HasDefaultValue(true).IsRequired();
        b.Property(x => x.LastReason).HasColumnName("lastReason").HasMaxLength(500);

        b.ToTable(t => t.HasCheckConstraint("CK_IntegrationSwitch_scope",
            "[scope] IN ('OutboundGlobal','InboundErpAck')"));

        b.HasIndex(x => new { x.TenantId, x.Scope })
            .HasDatabaseName("UQ_IntegrationSwitch_tenant_scope").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

/// <summary>R9 (§2.6) — immutable per-toggle audit: who/when (audit block), old → new, mandatory reason.</summary>
public class IntegrationSwitchAuditConfiguration : IEntityTypeConfiguration<IntegrationSwitchAudit>
{
    public void Configure(EntityTypeBuilder<IntegrationSwitchAudit> b)
    {
        b.ApplyBaseEntityConvention("IntegrationSwitchAudit", "integration", "integrationSwitchAudit");
        b.Property(x => x.IntegrationSwitchId).HasColumnName("integrationSwitchId");
        b.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(20).IsRequired();
        b.Property(x => x.OldEnabled).HasColumnName("oldEnabled").HasColumnType("bit");
        b.Property(x => x.NewEnabled).HasColumnName("newEnabled").HasColumnType("bit");
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();

        b.HasOne(x => x.IntegrationSwitch).WithMany().HasForeignKey(x => x.IntegrationSwitchId)
            .HasConstraintName("FK_IntegrationSwitchAudit_IntegrationSwitch_IntegrationSwitchId")
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IntegrationSwitchId).HasDatabaseName("IX_IntegrationSwitchAudit_switch");
    }
}

/// <summary>R9 (§2.5, migration 0048) — backfill run audit: dry-run snapshot + apply record, gateVersion-pinned.</summary>
public class LnBackfillRunConfiguration : IEntityTypeConfiguration<LnBackfillRun>
{
    public void Configure(EntityTypeBuilder<LnBackfillRun> b)
    {
        b.ApplyBaseEntityConvention("LnBackfillRun", "integration", "lnBackfillRun");
        b.Property(x => x.OutboundIntegrationConfigId).HasColumnName("lnEndpointConfigId");
        b.Property(x => x.TransactionType).HasColumnName("transactionType").HasMaxLength(60).IsRequired();
        b.Property(x => x.GateVersion).HasColumnName("gateVersion").HasColumnType("int");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(12).HasDefaultValue("DryRun").IsRequired();
        b.Property(x => x.EnqueueCount).HasColumnName("enqueueCount").HasColumnType("int").HasDefaultValue(0);
        b.Property(x => x.RearmCount).HasColumnName("rearmCount").HasColumnType("int").HasDefaultValue(0);
        b.Property(x => x.WithdrawCount).HasColumnName("withdrawCount").HasColumnType("int").HasDefaultValue(0);
        b.Property(x => x.DryRunResultJson).HasColumnName("dryRunResultJson").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.AppliedOn).HasColumnName("appliedOn").HasColumnType("datetime2");
        b.Property(x => x.AppliedBy).HasColumnName("appliedBy").HasMaxLength(100);
        b.Property(x => x.ApplyResultJson).HasColumnName("applyResultJson").HasColumnType("nvarchar(max)");

        b.ToTable(t => t.HasCheckConstraint("CK_LnBackfillRun_status",
            "[status] IN ('DryRun','Applied','Superseded','Discarded')"));

        b.HasOne(x => x.OutboundIntegrationConfig).WithMany().HasForeignKey(x => x.OutboundIntegrationConfigId)
            .HasConstraintName("FK_LnBackfillRun_OutboundIntegrationConfig_OutboundIntegrationConfigId")
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OutboundIntegrationConfigId, x.Status })
            .HasDatabaseName("IX_LnBackfillRun_config_status").HasFilter("[isDeleted] = 0");
    }
}

/// <summary>R9 (§2.6 inbound scope, migration 0048) — accept-and-hold store for erp-ack under kill; FIFO replay by Seq.</summary>
public class HeldInboundMessageConfiguration : IEntityTypeConfiguration<HeldInboundMessage>
{
    public void Configure(EntityTypeBuilder<HeldInboundMessage> b)
    {
        b.ApplyBaseEntityConvention("HeldInboundMessage", "integration", "heldInboundMessage");
        b.Property(x => x.EndpointName).HasColumnName("endpointName").HasMaxLength(40).HasDefaultValue("ErpAck").IsRequired();
        b.Property(x => x.PayloadJson).HasColumnName("payloadJson").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.IdempotencyKey).HasColumnName("idempotencyKey").HasMaxLength(128);
        b.Property(x => x.BoundCompanyIdsJson).HasColumnName("boundCompanyIdsJson").HasColumnType("nvarchar(max)");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(10).HasDefaultValue("Held").IsRequired();
        b.Property(x => x.ReplayAttempts).HasColumnName("replayAttempts").HasColumnType("int").HasDefaultValue(0);
        b.Property(x => x.ReplayedOn).HasColumnName("replayedOn").HasColumnType("datetime2");
        b.Property(x => x.LastError).HasColumnName("lastError").HasMaxLength(2000);

        b.ToTable(t => t.HasCheckConstraint("CK_HeldInboundMessage_status",
            "[status] IN ('Held','Replayed','Failed')"));

        // The replay worker's hot scan: live Held rows per tenant, FIFO by the clustered Seq.
        b.HasIndex(x => new { x.TenantId, x.Status })
            .HasDatabaseName("IX_HeldInboundMessage_tenant_status")
            .HasFilter("[status] = 'Held' AND [isDeleted] = 0");
    }
}
