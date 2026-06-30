using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0037_R5StatusMappingSyncLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PoStatusMapping",
                schema: "proc",
                columns: table => new
                {
                    poStatusMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    erpStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    poStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    poStatusMappingSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    rowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    seccodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoStatusMapping", x => x.poStatusMappingId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_PoStatusMapping_Seccode_SeccodeId",
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
                    direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Inbound"),
                    api = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    entityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    externalRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    errorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    receivedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    syncLogSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    rowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    seccodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
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
                name: "IX_PoStatusMapping_seccodeId",
                schema: "proc",
                table: "PoStatusMapping",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_PoStatusMapping_tenant_company",
                schema: "proc",
                table: "PoStatusMapping",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_PoStatusMapping_tenant_erp",
                schema: "proc",
                table: "PoStatusMapping",
                columns: new[] { "tenantId", "erpStatus" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_PoStatusMapping_poStatusMappingSeq",
                schema: "proc",
                table: "PoStatusMapping",
                column: "poStatusMappingSeq",
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PoStatusMapping",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "SyncLog",
                schema: "proc");
        }
    }
}
