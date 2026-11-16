using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0066_R19InvoiceDiscountGeneratedDocs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "discountAmount",
                schema: "proc",
                table: "InvoiceLine",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "discountPct",
                schema: "proc",
                table: "InvoiceLine",
                type: "decimal(9,4)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "isSystemGenerated",
                schema: "doc",
                table: "DocumentUpload",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discountAmount",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "discountPct",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "isSystemGenerated",
                schema: "doc",
                table: "DocumentUpload");
        }
    }
}
