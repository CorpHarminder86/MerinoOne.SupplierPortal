using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0057_R12GateActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "rejectedAt",
                schema: "proc",
                table: "PurchaseOrder",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "gateActivatedAt",
                schema: "integration",
                table: "OutboundIntegrationConfig",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rejectedAt",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "gateActivatedAt",
                schema: "integration",
                table: "OutboundIntegrationConfig");
        }
    }
}
