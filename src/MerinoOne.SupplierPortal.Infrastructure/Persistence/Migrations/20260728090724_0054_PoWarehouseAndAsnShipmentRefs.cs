using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0054_PoWarehouseAndAsnShipmentRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "warehouse",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billOfLading",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoiceNo",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "packingList",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warehouse",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "warehouse",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "billOfLading",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "invoiceNo",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "packingList",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "warehouse",
                schema: "proc",
                table: "Asn");
        }
    }
}
