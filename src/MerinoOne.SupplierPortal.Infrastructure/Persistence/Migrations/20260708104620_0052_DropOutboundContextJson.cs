using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0052_DropOutboundContextJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Option A (2026-07-08) — acl/entityName are now INLINE LITERALS in the IDM mapping catalogue; the
            // config.* context bag this contextJson column fed is gone. Rewrite existing Document rows that still
            // reference config.* to the same literals the edited .jsonata defaults carry, and re-stamp the per-slot
            // seed hashes to the new repo-default hash so a rewritten pristine row does not read as "drifted".
            // Gate on '%config.%': only config-referencing rows are touched (they MUST be rewritten — config.* no
            // longer resolves). A row hand-edited elsewhere keeps showing drift vs the new default, which is correct.
            migrationBuilder.Sql(@"
UPDATE [integration].[OutboundIntegrationConfig]
SET [requestMappingExpr]     = REPLACE(REPLACE([requestMappingExpr], 'config.acl', '""Public""'), 'config.entityName', '""MDS_GenericDocument""'),
    [requestMappingSeedHash] = CASE [targetEntityName]
        WHEN 'InforInvoice'                            THEN 'b7d76819773a2e06f68c825fe7866a5155ad2cea30c8f82416e6cd56c4162b63'
        WHEN 'InforAdvanceShipmentNoticeSupplierASN'   THEN 'd0ffbde38ca63be693a14db7bf7ecba96d4525843e2e4699f54f599ca62968de'
        ELSE [requestMappingSeedHash] END
WHERE [kind] = 'Document' AND [requestMappingExpr] LIKE '%config.%';

UPDATE [integration].[OutboundIntegrationConfig]
SET [mutateMappingExpr]     = REPLACE(REPLACE([mutateMappingExpr], 'config.acl', '""Public""'), 'config.entityName', '""MDS_GenericDocument""'),
    [mutateMappingSeedHash] = CASE [targetEntityName]
        WHEN 'InforInvoice'                            THEN 'b7d76819773a2e06f68c825fe7866a5155ad2cea30c8f82416e6cd56c4162b63'
        WHEN 'InforAdvanceShipmentNoticeSupplierASN'   THEN 'd0ffbde38ca63be693a14db7bf7ecba96d4525843e2e4699f54f599ca62968de'
        ELSE [mutateMappingSeedHash] END
WHERE [kind] = 'Document' AND [mutateMappingExpr] LIKE '%config.%';
");

            migrationBuilder.DropColumn(
                name: "contextJson",
                schema: "integration",
                table: "OutboundIntegrationConfig");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contextJson",
                schema: "integration",
                table: "OutboundIntegrationConfig",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
