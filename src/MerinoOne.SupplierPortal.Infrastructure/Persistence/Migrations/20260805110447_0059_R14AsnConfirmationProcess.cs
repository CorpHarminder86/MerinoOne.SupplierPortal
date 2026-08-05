using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0059_R14AsnConfirmationProcess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "asnConfirmationRequired",
                schema: "supplier",
                table: "Supplier",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "postedAt",
                schema: "proc",
                table: "Asn",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postedBy",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "asnConfirmationRequired",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "postedAt",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "postedBy",
                schema: "proc",
                table: "Asn");
        }
    }
}
