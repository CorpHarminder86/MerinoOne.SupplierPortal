using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

/// <summary>
/// R9 (TSD R9 §2.1, migration 0046) — <c>integration.LnEndpointConfig</c>: the per-transaction-type
/// config layer over the LN outbound pipeline. Two-key pattern + audit block via
/// <see cref="BaseEntityConfigExtensions.ApplyBaseEntityConvention"/>.
/// </summary>
public class LnEndpointConfigConfiguration : IEntityTypeConfiguration<LnEndpointConfig>
{
    public void Configure(EntityTypeBuilder<LnEndpointConfig> b)
    {
        b.ApplyBaseEntityConvention("LnEndpointConfig", "integration", "lnEndpointConfig");
        // tenantId mapped by the ITenantOwned block in ApplyBaseEntityConvention.
        b.Property(x => x.TransactionType).HasColumnName("transactionType").HasMaxLength(60).IsRequired();
        b.Property(x => x.PortalEntity).HasColumnName("portalEntity").HasMaxLength(60).IsRequired();
        b.Property(x => x.EndpointPath).HasColumnName("endpointPath").HasMaxLength(400).IsRequired();
        b.Property(x => x.HttpVerb).HasColumnName("httpVerb").HasMaxLength(10).HasDefaultValue("POST").IsRequired();
        // Tri-state cutover/kill (D-R9-2 + D-R9-11): CHECK-constrained enum name — Legacy must be the safe
        // default at row creation, so unlike most status enums this one gets a DB CHECK (matches R8's
        // OutboundEndpointConfig.httpMethod precedent for config-table value guards).
        b.Property(x => x.DispatchMode).HasColumnName("dispatchMode").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(LnDispatchMode.Legacy).IsRequired();
        b.Property(x => x.EligibilityGateExpr).HasColumnName("eligibilityGateExpr").HasColumnType("nvarchar(max)");
        b.Property(x => x.RequestMappingExpr).HasColumnName("requestMappingExpr").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.ResponseMappingExpr).HasColumnName("responseMappingExpr").HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.AckMappingExpr).HasColumnName("ackMappingExpr").HasColumnType("nvarchar(max)");
        b.Property(x => x.RequestMappingSeedHash).HasColumnName("requestMappingSeedHash").HasMaxLength(64);
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

        // Verb + mode value guards (config table — hostile values here mean wrong HTTP calls, so DB CHECKs
        // back the C# enums; mirrors R8's CK on OutboundEndpointConfig.httpMethod).
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_LnEndpointConfig_httpVerb", "[httpVerb] IN ('POST','PUT','PATCH')");
            t.HasCheckConstraint("CK_LnEndpointConfig_dispatchMode", "[dispatchMode] IN ('Legacy','Dynamic','Held')");
        });

        // One live config per (tenant, transactionType) — filtered so a soft-deleted row (rollback-to-legacy)
        // never blocks re-creating the config. Matches the outbox filtered-index tenancy approach.
        b.HasIndex(x => new { x.TenantId, x.TransactionType })
            .HasDatabaseName("UQ_LnEndpointConfig_tenant_transactionType").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}
