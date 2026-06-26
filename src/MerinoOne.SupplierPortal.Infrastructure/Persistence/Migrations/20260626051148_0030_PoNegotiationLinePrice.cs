using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0030_PoNegotiationLinePrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "negotiatedPrice",
                schema: "proc",
                table: "PurchaseOrderNegotiationLine",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "originalPrice",
                schema: "proc",
                table: "PurchaseOrderNegotiationLine",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "negotiatedPrice",
                schema: "proc",
                table: "PurchaseOrderNegotiationLine");

            migrationBuilder.DropColumn(
                name: "originalPrice",
                schema: "proc",
                table: "PurchaseOrderNegotiationLine");
        }
    }
}
