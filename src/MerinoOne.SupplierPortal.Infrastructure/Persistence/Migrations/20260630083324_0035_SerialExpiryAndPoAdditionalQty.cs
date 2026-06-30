using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0035_SerialExpiryAndPoAdditionalQty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "additionalQty",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "expiryDate",
                schema: "proc",
                table: "AsnLineSerial",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "additionalQty",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "expiryDate",
                schema: "proc",
                table: "AsnLineSerial");
        }
    }
}
