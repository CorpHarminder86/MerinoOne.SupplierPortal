using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccessAndIntegrationEnhancements_0013 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allSuppliers",
                schema: "admin",
                table: "UserCompanyMap",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "entityCount",
                schema: "integration",
                table: "InforSyncLog",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "entityId",
                schema: "integration",
                table: "InforSyncLog",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payloadJson",
                schema: "integration",
                table: "InforSyncLog",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "retryCount",
                schema: "integration",
                table: "InforSyncLog",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ApiKeyCompany",
                schema: "integration",
                columns: table => new
                {
                    apiKeyCompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    apiKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    apiKeyCompanySeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyCompany", x => x.apiKeyCompanyId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_ApiKeyCompany_ApiKey_ApiKeyId",
                        column: x => x.apiKeyId,
                        principalSchema: "integration",
                        principalTable: "ApiKey",
                        principalColumn: "apiKeyId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApiKeyCompany_TenantEntity_TenantEntityId",
                        column: x => x.tenantEntityId,
                        principalSchema: "admin",
                        principalTable: "TenantEntity",
                        principalColumn: "tenantEntityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyCompany_tenantEntityId",
                schema: "integration",
                table: "ApiKeyCompany",
                column: "tenantEntityId");

            migrationBuilder.CreateIndex(
                name: "UQ_ApiKeyCompany_apiKey_company",
                schema: "integration",
                table: "ApiKeyCompany",
                columns: new[] { "apiKeyId", "tenantEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ApiKeyCompany_apiKeyCompanySeq",
                schema: "integration",
                table: "ApiKeyCompany",
                column: "apiKeyCompanySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            // --- Feature C data move: migrate the legacy single ApiKey.tenantEntityId binding into the
            // new multi-company junction. One row per existing key that has a bound company. Seq is an
            // IDENTITY column so it is omitted; apiKeyCompanyId defaults via NEWID(); the tenant + audit
            // block are copied from the source key so the row carries correct scope/audit. createdBy is
            // 'seed' so the AuditableEntityInterceptor short-circuits if this row is ever re-saved. The
            // ApiKey.tenantEntityId column is kept transitionally (still mapped) — readers migrate to the
            // junction in a backend follow-up, and migration _0014 drops it once they are off it.
            migrationBuilder.Sql(@"
INSERT INTO integration.apiKeyCompany (tenantId, apiKeyId, tenantEntityId, createdOn, createdBy, isDeleted)
SELECT k.tenantId, k.apiKeyId, k.tenantEntityId, SYSUTCDATETIME(), 'seed', 0
FROM integration.apiKey AS k
WHERE k.tenantEntityId IS NOT NULL
  AND k.isDeleted = 0
  AND NOT EXISTS (
        SELECT 1 FROM integration.apiKeyCompany AS c
        WHERE c.apiKeyId = k.apiKeyId AND c.tenantEntityId = k.tenantEntityId
  );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeyCompany",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "allSuppliers",
                schema: "admin",
                table: "UserCompanyMap");

            migrationBuilder.DropColumn(
                name: "entityCount",
                schema: "integration",
                table: "InforSyncLog");

            migrationBuilder.DropColumn(
                name: "entityId",
                schema: "integration",
                table: "InforSyncLog");

            migrationBuilder.DropColumn(
                name: "payloadJson",
                schema: "integration",
                table: "InforSyncLog");

            migrationBuilder.DropColumn(
                name: "retryCount",
                schema: "integration",
                table: "InforSyncLog");
        }
    }
}
