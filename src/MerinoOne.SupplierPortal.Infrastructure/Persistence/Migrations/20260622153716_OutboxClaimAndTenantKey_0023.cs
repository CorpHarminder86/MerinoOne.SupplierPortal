using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxClaimAndTenantKey_0023 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_OutboxMessage_deterministicKey",
                schema: "integration",
                table: "OutboxMessage");

            migrationBuilder.AddColumn<byte[]>(
                name: "rowVersion",
                schema: "integration",
                table: "OutboxMessage",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<DateTime>(
                name: "erpPostInitiatedAt",
                schema: "proc",
                table: "Invoice",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UQ_OutboxMessage_tenant_deterministicKey",
                schema: "integration",
                table: "OutboxMessage",
                columns: new[] { "tenantId", "deterministicKey" },
                unique: true,
                filter: "[isDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_OutboxMessage_tenant_deterministicKey",
                schema: "integration",
                table: "OutboxMessage");

            migrationBuilder.DropColumn(
                name: "rowVersion",
                schema: "integration",
                table: "OutboxMessage");

            migrationBuilder.DropColumn(
                name: "erpPostInitiatedAt",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.CreateIndex(
                name: "UQ_OutboxMessage_deterministicKey",
                schema: "integration",
                table: "OutboxMessage",
                column: "deterministicKey",
                unique: true,
                filter: "[isDeleted] = 0");
        }
    }
}
