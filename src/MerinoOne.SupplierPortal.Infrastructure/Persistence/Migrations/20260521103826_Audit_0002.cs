using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Audit_0002 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "AuditEntry",
                schema: "audit",
                columns: table => new
                {
                    auditEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    entityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    entityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    operation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    fieldName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    oldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    newValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    changedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    changedOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    auditEntrySeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntry", x => x.auditEntryId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_AuditEntry_operation", "[operation] IN ('Insert','Update','Delete')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntry_entity",
                schema: "audit",
                table: "AuditEntry",
                columns: new[] { "entityName", "entityId", "changedOn" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "UX_AuditEntry_auditEntrySeq",
                schema: "audit",
                table: "AuditEntry",
                column: "auditEntrySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntry",
                schema: "audit");
        }
    }
}
