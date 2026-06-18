using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropItemUom_0016 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "uom",
                schema: "inv",
                table: "Item");

            migrationBuilder.AddColumn<string>(
                name: "area",
                schema: "supplier",
                table: "SupplierAddress",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "company",
                schema: "integration",
                table: "InforConnectionSetting",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "area",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.AddColumn<string>(
                name: "uom",
                schema: "inv",
                table: "Item",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "EA");

            migrationBuilder.AlterColumn<string>(
                name: "company",
                schema: "integration",
                table: "InforConnectionSetting",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
