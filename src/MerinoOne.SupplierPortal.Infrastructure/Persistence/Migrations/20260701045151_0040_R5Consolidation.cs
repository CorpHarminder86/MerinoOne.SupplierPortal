using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0040_R5Consolidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CompanyAddress_Company_companyId",
                schema: "admin",
                table: "CompanyAddress");

            // [[r5-consolidation]] — CompanyAddress moves from admin.Company to admin.TenantEntity. EF renames the
            // FK column companyId -> tenantEntityId (preserving the column data), but the STORED VALUES are old
            // admin.Company ids and must be remapped to the TenantEntity id (admin.Company.tenantEntityId) BEFORE
            // admin.Company is dropped and BEFORE the new FK to admin.TenantEntity is added. The FK was just dropped
            // above, so this in-place remap is unconstrained. Every seeded/created Company carries a non-null
            // tenantEntityId, so the inner join remaps every address exactly once.
            migrationBuilder.Sql(@"
                UPDATE ca
                SET ca.companyId = c.tenantEntityId
                FROM admin.CompanyAddress AS ca
                INNER JOIN admin.Company AS c ON ca.companyId = c.companyId;");

            migrationBuilder.DropTable(
                name: "Company",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "SyncLog",
                schema: "proc");

            migrationBuilder.RenameColumn(
                name: "companyId",
                schema: "admin",
                table: "CompanyAddress",
                newName: "tenantEntityId");

            migrationBuilder.RenameIndex(
                name: "UQ_CompanyAddress_company_erp",
                schema: "admin",
                table: "CompanyAddress",
                newName: "UQ_CompanyAddress_tenantEntity_erp");

            migrationBuilder.RenameIndex(
                name: "IX_CompanyAddress_company",
                schema: "admin",
                table: "CompanyAddress",
                newName: "IX_CompanyAddress_tenantEntity");

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyAddress_TenantEntity_tenantEntityId",
                schema: "admin",
                table: "CompanyAddress",
                column: "tenantEntityId",
                principalSchema: "admin",
                principalTable: "TenantEntity",
                principalColumn: "tenantEntityId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CompanyAddress_TenantEntity_tenantEntityId",
                schema: "admin",
                table: "CompanyAddress");

            migrationBuilder.RenameColumn(
                name: "tenantEntityId",
                schema: "admin",
                table: "CompanyAddress",
                newName: "companyId");

            migrationBuilder.RenameIndex(
                name: "UQ_CompanyAddress_tenantEntity_erp",
                schema: "admin",
                table: "CompanyAddress",
                newName: "UQ_CompanyAddress_company_erp");

            migrationBuilder.RenameIndex(
                name: "IX_CompanyAddress_tenantEntity",
                schema: "admin",
                table: "CompanyAddress",
                newName: "IX_CompanyAddress_company");

            migrationBuilder.CreateTable(
                name: "Company",
                schema: "admin",
                columns: table => new
                {
                    companyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    seccodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    rowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    companySeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Company", x => x.companyId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Company_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncLog",
                schema: "proc",
                columns: table => new
                {
                    syncLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    seccodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    api = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Inbound"),
                    entityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    errorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    externalRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    receivedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    rowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    syncLogSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLog", x => x.syncLogId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SyncLog_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Company_seccodeId",
                schema: "admin",
                table: "Company",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "UQ_Company_tenant_entity",
                schema: "admin",
                table: "Company",
                columns: new[] { "tenantId", "tenantEntityId" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Company_companySeq",
                schema: "admin",
                table: "Company",
                column: "companySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncLog_ref",
                schema: "proc",
                table: "SyncLog",
                column: "externalRef");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLog_seccodeId",
                schema: "proc",
                table: "SyncLog",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLog_status_date",
                schema: "proc",
                table: "SyncLog",
                columns: new[] { "status", "receivedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_SyncLog_syncLogSeq",
                schema: "proc",
                table: "SyncLog",
                column: "syncLogSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyAddress_Company_companyId",
                schema: "admin",
                table: "CompanyAddress",
                column: "companyId",
                principalSchema: "admin",
                principalTable: "Company",
                principalColumn: "companyId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
