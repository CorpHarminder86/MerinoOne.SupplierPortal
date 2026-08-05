using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0058_R13WarehousePerLineBaseAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Asn_CompanyAddress_shipToAddressId",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropForeignKey(
                name: "FK_DeliverySchedule_CompanyAddress_ShipToAddressId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrder_CompanyAddress_shipToAddressId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrder_shipTo",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropIndex(
                name: "IX_DeliverySchedule_shipTo_date",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "shipToAddressId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToAddressName",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToCity",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToCountry",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToErpCode",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToLine1",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToLine2",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToPincode",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToState",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "warehouse",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToAddressId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.RenameColumn(
                name: "shipToAddressId",
                schema: "proc",
                table: "Asn",
                newName: "warehouseAddressId");

            migrationBuilder.RenameIndex(
                name: "IX_Asn_shipTo",
                schema: "proc",
                table: "Asn",
                newName: "IX_Asn_warehouse");

            migrationBuilder.AddColumn<string>(
                name: "warehouse",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "warehouseAddressId",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whAddressName",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whCity",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whCountry",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whErpCode",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whLine1",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whLine2",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whPincode",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whState",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "warehouseAddressId",
                schema: "proc",
                table: "DeliverySchedule",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "isBaseAddress",
                schema: "admin",
                table: "CompanyAddress",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "whAddressName",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whCity",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whCountry",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whErpCode",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whLine1",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whLine2",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whPincode",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whState",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_warehouse",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "warehouseAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliverySchedule_warehouse_date",
                schema: "proc",
                table: "DeliverySchedule",
                columns: new[] { "warehouseAddressId", "deliveryDate" });

            migrationBuilder.CreateIndex(
                name: "UQ_CompanyAddress_tenantEntity_base",
                schema: "admin",
                table: "CompanyAddress",
                columns: new[] { "tenantEntityId", "isBaseAddress" },
                unique: true,
                filter: "[isBaseAddress] = 1 AND [isDeleted] = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_Asn_CompanyAddress_warehouseAddressId",
                schema: "proc",
                table: "Asn",
                column: "warehouseAddressId",
                principalSchema: "admin",
                principalTable: "CompanyAddress",
                principalColumn: "companyAddressId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DeliverySchedule_CompanyAddress_WarehouseAddressId",
                schema: "proc",
                table: "DeliverySchedule",
                column: "warehouseAddressId",
                principalSchema: "admin",
                principalTable: "CompanyAddress",
                principalColumn: "companyAddressId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderLine_CompanyAddress_warehouseAddressId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "warehouseAddressId",
                principalSchema: "admin",
                principalTable: "CompanyAddress",
                principalColumn: "companyAddressId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Asn_CompanyAddress_warehouseAddressId",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropForeignKey(
                name: "FK_DeliverySchedule_CompanyAddress_WarehouseAddressId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrderLine_CompanyAddress_warehouseAddressId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderLine_warehouse",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropIndex(
                name: "IX_DeliverySchedule_warehouse_date",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropIndex(
                name: "UQ_CompanyAddress_tenantEntity_base",
                schema: "admin",
                table: "CompanyAddress");

            migrationBuilder.DropColumn(
                name: "warehouse",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "warehouseAddressId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "whAddressName",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "whCity",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "whCountry",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "whErpCode",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "whLine1",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "whLine2",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "whPincode",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "whState",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "warehouseAddressId",
                schema: "proc",
                table: "DeliverySchedule");

            migrationBuilder.DropColumn(
                name: "isBaseAddress",
                schema: "admin",
                table: "CompanyAddress");

            migrationBuilder.DropColumn(
                name: "whAddressName",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "whCity",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "whCountry",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "whErpCode",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "whLine1",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "whLine2",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "whPincode",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "whState",
                schema: "proc",
                table: "Asn");

            migrationBuilder.RenameColumn(
                name: "warehouseAddressId",
                schema: "proc",
                table: "Asn",
                newName: "shipToAddressId");

            migrationBuilder.RenameIndex(
                name: "IX_Asn_warehouse",
                schema: "proc",
                table: "Asn",
                newName: "IX_Asn_shipTo");

            migrationBuilder.AddColumn<Guid>(
                name: "shipToAddressId",
                schema: "proc",
                table: "PurchaseOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToAddressName",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToCity",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToCountry",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToErpCode",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToLine1",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToLine2",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToPincode",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToState",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warehouse",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "shipToAddressId",
                schema: "proc",
                table: "DeliverySchedule",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_shipTo",
                schema: "proc",
                table: "PurchaseOrder",
                column: "shipToAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliverySchedule_shipTo_date",
                schema: "proc",
                table: "DeliverySchedule",
                columns: new[] { "shipToAddressId", "deliveryDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_Asn_CompanyAddress_shipToAddressId",
                schema: "proc",
                table: "Asn",
                column: "shipToAddressId",
                principalSchema: "admin",
                principalTable: "CompanyAddress",
                principalColumn: "companyAddressId",
                onDelete: ReferentialAction.Restrict);

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
                name: "FK_PurchaseOrder_CompanyAddress_shipToAddressId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "shipToAddressId",
                principalSchema: "admin",
                principalTable: "CompanyAddress",
                principalColumn: "companyAddressId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
