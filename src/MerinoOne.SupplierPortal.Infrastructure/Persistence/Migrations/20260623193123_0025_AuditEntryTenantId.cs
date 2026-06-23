using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0025_AuditEntryTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "audit",
                table: "AuditEntry",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntry_tenantId",
                schema: "audit",
                table: "AuditEntry",
                column: "tenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEntry_tenantId",
                schema: "audit",
                table: "AuditEntry");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "audit",
                table: "AuditEntry");
        }
    }
}
