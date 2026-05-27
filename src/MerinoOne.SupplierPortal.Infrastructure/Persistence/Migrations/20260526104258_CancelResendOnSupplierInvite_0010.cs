using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CancelResendOnSupplierInvite_0010 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                schema: "admin",
                table: "SupplierInvite",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledBy",
                schema: "admin",
                table: "SupplierInvite",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastResentAt",
                schema: "admin",
                table: "SupplierInvite",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResendCount",
                schema: "admin",
                table: "SupplierInvite",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelledAt",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropColumn(
                name: "CancelledBy",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropColumn(
                name: "LastResentAt",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropColumn(
                name: "ResendCount",
                schema: "admin",
                table: "SupplierInvite");
        }
    }
}
