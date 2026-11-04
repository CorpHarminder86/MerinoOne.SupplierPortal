using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0065_R18AsnEwayIrnNegotiationRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parentNegotiationId",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "proposedBy",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Supplier");

            migrationBuilder.AddColumn<int>(
                name: "roundNo",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ewayBillDate",
                schema: "proc",
                table: "Asn",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ewayBillNo",
                schema: "proc",
                table: "Asn",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "irnDate",
                schema: "proc",
                table: "Asn",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "irnNo",
                schema: "proc",
                table: "Asn",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderNegotiation_parentNegotiationId",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                column: "parentNegotiationId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderNegotiation_PurchaseOrderNegotiation_parentNegotiationId",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                column: "parentNegotiationId",
                principalSchema: "proc",
                principalTable: "PurchaseOrderNegotiation",
                principalColumn: "purchaseOrderNegotiationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrderNegotiation_PurchaseOrderNegotiation_parentNegotiationId",
                schema: "proc",
                table: "PurchaseOrderNegotiation");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderNegotiation_parentNegotiationId",
                schema: "proc",
                table: "PurchaseOrderNegotiation");

            migrationBuilder.DropColumn(
                name: "parentNegotiationId",
                schema: "proc",
                table: "PurchaseOrderNegotiation");

            migrationBuilder.DropColumn(
                name: "proposedBy",
                schema: "proc",
                table: "PurchaseOrderNegotiation");

            migrationBuilder.DropColumn(
                name: "roundNo",
                schema: "proc",
                table: "PurchaseOrderNegotiation");

            migrationBuilder.DropColumn(
                name: "ewayBillDate",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "ewayBillNo",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "irnDate",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "irnNo",
                schema: "proc",
                table: "Asn");
        }
    }
}
