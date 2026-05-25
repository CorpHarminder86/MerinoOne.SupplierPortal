using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MobileNoOnSupplierInvite_0007 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mobileNo",
                schema: "admin",
                table: "SupplierInvite",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mobileNo",
                schema: "admin",
                table: "SupplierInvite");
        }
    }
}
