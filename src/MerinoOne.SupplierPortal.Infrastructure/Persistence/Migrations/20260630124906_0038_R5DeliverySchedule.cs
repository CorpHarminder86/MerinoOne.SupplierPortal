using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0038_R5DeliverySchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // R5 reshapes proc.DeliverySchedule (adds purchaseOrderLineId, shipToAddressId, scheduledQty,
            // deliveryDate; drops pre-R5 fields). Existing rows are pre-R5 dev/test data with no valid
            // purchaseOrderLineId or shipToAddressId — they cannot be backfilled and have no production value.
            // Truncate first so the NOT NULL columns and FK constraints can be added cleanly.
            // (Spec §8.1: schedules are created by the Application layer on PO-becomes-shippable; the
            // migration itself never seeds schedule rows.)
            migrationBuilder.Sql("DELETE FROM [proc].[DeliverySchedule];");

            migrationBuilder.DropForeignKey(
                name: "FK_DeliverySchedule_PurchaseOrder_PurchaseOrderId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "approvedBy",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "rejectionReason",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "scheduleStatus",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "timeWindow",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "vehicleInfo",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.RenameColumn(
                name: "proposedDate",
                schema: "proc",
                table: "DeliverySchedule",
                newName: "deliveryDate");

            migrationBuilder.AddColumn<Guid>(
                name: "purchaseOrderLineId",
                schema: "proc",
                table: "DeliverySchedule",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<decimal>(
                name: "scheduledQty",
                schema: "proc",
                table: "DeliverySchedule",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "shipToAddressId",
                schema: "proc",
                table: "DeliverySchedule",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "proc",
                table: "DeliverySchedule",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_DeliverySchedule_shipTo_date",
                schema: "proc",
                table: "DeliverySchedule",
                columns: new[] { "shipToAddressId", "deliveryDate" });

            migrationBuilder.CreateIndex(
                name: "UQ_DeliverySchedule_line",
                schema: "proc",
                table: "DeliverySchedule",
                column: "purchaseOrderLineId",
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_DeliverySchedule_CompanyAddress_ShipToAddressId",
                schema: "proc",
                table: "DeliverySchedule",
                column: "shipToAddressId",
                principalSchema: "admin",
                principalTable: "CompanyAddress",
                principalColumn: "companyAddressId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DeliverySchedule_PurchaseOrderLine_PurchaseOrderLineId",
                schema: "proc",
                table: "DeliverySchedule",
                column: "purchaseOrderLineId",
                principalSchema: "proc",
                principalTable: "PurchaseOrderLine",
                principalColumn: "purchaseOrderLineId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DeliverySchedule_PurchaseOrder_PurchaseOrderId",
                schema: "proc",
                table: "DeliverySchedule",
                column: "purchaseOrderId",
                principalSchema: "proc",
                principalTable: "PurchaseOrder",
                principalColumn: "purchaseOrderId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DeliverySchedule_CompanyAddress_ShipToAddressId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropForeignKey(
                name: "FK_DeliverySchedule_PurchaseOrderLine_PurchaseOrderLineId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropForeignKey(
                name: "FK_DeliverySchedule_PurchaseOrder_PurchaseOrderId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropIndex(
                name: "IX_DeliverySchedule_shipTo_date",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropIndex(
                name: "UQ_DeliverySchedule_line",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "purchaseOrderLineId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "scheduledQty",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "shipToAddressId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.RenameColumn(
                name: "deliveryDate",
                schema: "proc",
                table: "DeliverySchedule",
                newName: "proposedDate");

            migrationBuilder.AddColumn<string>(
                name: "approvedBy",
                schema: "proc",
                table: "DeliverySchedule",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejectionReason",
                schema: "proc",
                table: "DeliverySchedule",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scheduleStatus",
                schema: "proc",
                table: "DeliverySchedule",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "timeWindow",
                schema: "proc",
                table: "DeliverySchedule",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vehicleInfo",
                schema: "proc",
                table: "DeliverySchedule",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_DeliverySchedule_PurchaseOrder_PurchaseOrderId",
                schema: "proc",
                table: "DeliverySchedule",
                column: "purchaseOrderId",
                principalSchema: "proc",
                principalTable: "PurchaseOrder",
                principalColumn: "purchaseOrderId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
