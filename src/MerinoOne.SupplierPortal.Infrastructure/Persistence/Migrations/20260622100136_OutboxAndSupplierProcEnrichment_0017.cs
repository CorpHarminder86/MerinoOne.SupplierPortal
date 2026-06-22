using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OutboxAndSupplierProcEnrichment_0017 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "erpCode",
                schema: "supplier",
                table: "SupplierContact",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpCode",
                schema: "supplier",
                table: "SupplierAddress",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "currencyId",
                schema: "supplier",
                table: "Supplier",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deliveryTermCode",
                schema: "supplier",
                table: "Supplier",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deliveryTermId",
                schema: "supplier",
                table: "Supplier",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpCode",
                schema: "supplier",
                table: "Supplier",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "paymentTermCode",
                schema: "supplier",
                table: "Supplier",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "paymentTermId",
                schema: "supplier",
                table: "Supplier",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "poResponseMode",
                schema: "supplier",
                table: "Supplier",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<Guid>(
                name: "taxId",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currencyCode",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "currencyId",
                schema: "proc",
                table: "PurchaseOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "isLotControlled",
                schema: "inv",
                table: "Item",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "isSerialized",
                schema: "inv",
                table: "Item",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "integration",
                columns: table => new
                {
                    outboxMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    transactionType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    entityName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    entityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    deterministicKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    payloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    attemptCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    dispatchedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ackedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    lastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    outboxMessageSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_OutboxMessage", x => x.outboxMessageId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "SupplierBankDetail",
                schema: "supplier",
                columns: table => new
                {
                    supplierBankDetailId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    bankName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    bankAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    accountName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    accountNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    currencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ifscCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    swiftCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    isPrimary = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    erpCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    supplierBankDetailSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    rowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    seccodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBankDetail", x => x.supplierBankDetailId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SupplierBankDetail_Currency_CurrencyId",
                        column: x => x.currencyId,
                        principalSchema: "mdm",
                        principalTable: "Currency",
                        principalColumn: "currencyId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierBankDetail_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierBankDetail_Supplier_SupplierId",
                        column: x => x.supplierId,
                        principalSchema: "supplier",
                        principalTable: "Supplier",
                        principalColumn: "supplierId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierLicense",
                schema: "supplier",
                columns: table => new
                {
                    supplierLicenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    licenseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    licenseType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    issueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    expiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    erpCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    supplierLicenseSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    rowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    seccodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierLicense", x => x.supplierLicenseId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SupplierLicense_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierLicense_Supplier_SupplierId",
                        column: x => x.supplierId,
                        principalSchema: "supplier",
                        principalTable: "Supplier",
                        principalColumn: "supplierId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tax",
                schema: "proc",
                columns: table => new
                {
                    taxId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    taxRate = table.Column<decimal>(type: "decimal(9,4)", nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    taxSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Tax", x => x.taxId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_currencyId",
                schema: "supplier",
                table: "Supplier",
                column: "currencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_deliveryTermId",
                schema: "supplier",
                table: "Supplier",
                column: "deliveryTermId");

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_paymentTermId",
                schema: "supplier",
                table: "Supplier",
                column: "paymentTermId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_taxId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "taxId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_currencyId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "currencyId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_status",
                schema: "integration",
                table: "OutboxMessage",
                column: "status",
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UQ_OutboxMessage_deterministicKey",
                schema: "integration",
                table: "OutboxMessage",
                column: "deterministicKey",
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_OutboxMessage_outboxMessageSeq",
                schema: "integration",
                table: "OutboxMessage",
                column: "outboxMessageSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBankDetail_currencyId",
                schema: "supplier",
                table: "SupplierBankDetail",
                column: "currencyId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBankDetail_seccodeId",
                schema: "supplier",
                table: "SupplierBankDetail",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBankDetail_supplierId",
                schema: "supplier",
                table: "SupplierBankDetail",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "UQ_SupplierBankDetail_supplier_account",
                schema: "supplier",
                table: "SupplierBankDetail",
                columns: new[] { "supplierId", "accountNumber" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierBankDetail_supplierBankDetailSeq",
                schema: "supplier",
                table: "SupplierBankDetail",
                column: "supplierBankDetailSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLicense_expiry",
                schema: "supplier",
                table: "SupplierLicense",
                column: "expiryDate",
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLicense_seccodeId",
                schema: "supplier",
                table: "SupplierLicense",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierLicense_supplierId",
                schema: "supplier",
                table: "SupplierLicense",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierLicense_supplierLicenseSeq",
                schema: "supplier",
                table: "SupplierLicense",
                column: "supplierLicenseSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Tax_tenant_company",
                schema: "proc",
                table: "Tax",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_Tax_company_code",
                schema: "proc",
                table: "Tax",
                columns: new[] { "tenantEntityId", "code" },
                unique: true,
                filter: "[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Tax_taxSeq",
                schema: "proc",
                table: "Tax",
                column: "taxSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrder_Currency_CurrencyId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "currencyId",
                principalSchema: "mdm",
                principalTable: "Currency",
                principalColumn: "currencyId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderLine_Tax_TaxId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "taxId",
                principalSchema: "proc",
                principalTable: "Tax",
                principalColumn: "taxId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Supplier_Currency_CurrencyId",
                schema: "supplier",
                table: "Supplier",
                column: "currencyId",
                principalSchema: "mdm",
                principalTable: "Currency",
                principalColumn: "currencyId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Supplier_DeliveryTerm_DeliveryTermId",
                schema: "supplier",
                table: "Supplier",
                column: "deliveryTermId",
                principalSchema: "proc",
                principalTable: "DeliveryTerm",
                principalColumn: "deliveryTermId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Supplier_PaymentTerm_PaymentTermId",
                schema: "supplier",
                table: "Supplier",
                column: "paymentTermId",
                principalSchema: "proc",
                principalTable: "PaymentTerm",
                principalColumn: "paymentTermId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrder_Currency_CurrencyId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrderLine_Tax_TaxId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropForeignKey(
                name: "FK_Supplier_Currency_CurrencyId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropForeignKey(
                name: "FK_Supplier_DeliveryTerm_DeliveryTermId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropForeignKey(
                name: "FK_Supplier_PaymentTerm_PaymentTermId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "SupplierBankDetail",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "SupplierLicense",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "Tax",
                schema: "proc");

            migrationBuilder.DropIndex(
                name: "IX_Supplier_currencyId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "IX_Supplier_deliveryTermId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "IX_Supplier_paymentTermId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderLine_taxId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrder_currencyId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "erpCode",
                schema: "supplier",
                table: "SupplierContact");

            migrationBuilder.DropColumn(
                name: "erpCode",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropColumn(
                name: "currencyId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "deliveryTermCode",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "deliveryTermId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "erpCode",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "paymentTermCode",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "paymentTermId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "poResponseMode",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "taxId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "currencyCode",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "currencyId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "isLotControlled",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "isSerialized",
                schema: "inv",
                table: "Item");
        }
    }
}
