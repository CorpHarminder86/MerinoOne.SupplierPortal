using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0055_DropAsnErpComposite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "erpCompany",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "erpDocumentNo",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "erpTransactionType",
                schema: "proc",
                table: "Asn");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "erpCompany",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpDocumentNo",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpTransactionType",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }
    }
}
