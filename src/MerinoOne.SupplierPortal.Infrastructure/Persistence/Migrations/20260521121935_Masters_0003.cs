using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Masters_0003 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inv");

            migrationBuilder.AddColumn<Guid>(
                name: "itemId",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deliveryTermId",
                schema: "proc",
                table: "PurchaseOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "paymentTermId",
                schema: "proc",
                table: "PurchaseOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "itemId",
                schema: "proc",
                table: "InvoiceLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "itemId",
                schema: "proc",
                table: "AsnLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeliveryTerm",
                schema: "proc",
                columns: table => new
                {
                    deliveryTermId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    deliveryTermSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryTerm", x => x.deliveryTermId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Item",
                schema: "inv",
                columns: table => new
                {
                    itemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    uom = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "EA"),
                    hsnCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    itemSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Item", x => x.itemId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "PaymentTerm",
                schema: "proc",
                columns: table => new
                {
                    paymentTermId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    netDays = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    paymentTermSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTerm", x => x.paymentTermId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "SupplierInvite",
                schema: "admin",
                columns: table => new
                {
                    supplierInviteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    legalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    invitedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    invitedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    expiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    consumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    supplierInviteSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierInvite", x => x.supplierInviteId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_itemId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "itemId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_deliveryTermId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "deliveryTermId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_paymentTermId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "paymentTermId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_itemId",
                schema: "proc",
                table: "InvoiceLine",
                column: "itemId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnLine_itemId",
                schema: "proc",
                table: "AsnLine",
                column: "itemId");

            migrationBuilder.CreateIndex(
                name: "UQ_DeliveryTerm_code",
                schema: "proc",
                table: "DeliveryTerm",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DeliveryTerm_deliveryTermSeq",
                schema: "proc",
                table: "DeliveryTerm",
                column: "deliveryTermSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_Item_code",
                schema: "inv",
                table: "Item",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Item_itemSeq",
                schema: "inv",
                table: "Item",
                column: "itemSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_PaymentTerm_code",
                schema: "proc",
                table: "PaymentTerm",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PaymentTerm_paymentTermSeq",
                schema: "proc",
                table: "PaymentTerm",
                column: "paymentTermSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvite_email",
                schema: "admin",
                table: "SupplierInvite",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "UQ_SupplierInvite_token",
                schema: "admin",
                table: "SupplierInvite",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SupplierInvite_supplierInviteSeq",
                schema: "admin",
                table: "SupplierInvite",
                column: "supplierInviteSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_AsnLine_Item_ItemId",
                schema: "proc",
                table: "AsnLine",
                column: "itemId",
                principalSchema: "inv",
                principalTable: "Item",
                principalColumn: "itemId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceLine_Item_ItemId",
                schema: "proc",
                table: "InvoiceLine",
                column: "itemId",
                principalSchema: "inv",
                principalTable: "Item",
                principalColumn: "itemId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrder_DeliveryTerm_DeliveryTermId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "deliveryTermId",
                principalSchema: "proc",
                principalTable: "DeliveryTerm",
                principalColumn: "deliveryTermId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrder_PaymentTerm_PaymentTermId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "paymentTermId",
                principalSchema: "proc",
                principalTable: "PaymentTerm",
                principalColumn: "paymentTermId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderLine_Item_ItemId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "itemId",
                principalSchema: "inv",
                principalTable: "Item",
                principalColumn: "itemId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AsnLine_Item_ItemId",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceLine_Item_ItemId",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrder_DeliveryTerm_DeliveryTermId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrder_PaymentTerm_PaymentTermId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrderLine_Item_ItemId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropTable(
                name: "DeliveryTerm",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "Item",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "PaymentTerm",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "SupplierInvite",
                schema: "admin");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderLine_itemId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrder_deliveryTermId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrder_paymentTermId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLine_itemId",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropIndex(
                name: "IX_AsnLine_itemId",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropColumn(
                name: "itemId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "deliveryTermId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "paymentTermId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "itemId",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "itemId",
                schema: "proc",
                table: "AsnLine");
        }
    }
}
