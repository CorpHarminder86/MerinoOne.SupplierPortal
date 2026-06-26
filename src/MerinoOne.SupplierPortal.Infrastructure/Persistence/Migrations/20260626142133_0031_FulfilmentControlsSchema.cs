using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0031_FulfilmentControlsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allowNegotiate",
                schema: "supplier",
                table: "Supplier",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "allowReject",
                schema: "supplier",
                table: "Supplier",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "shippedQtyToDate",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            // TSD R4 Addendum §3.1 — one-time backfill of the cumulative cache from Σ AsnLine.shippedQty over
            // all NON-cancelled, non-deleted ASN lines for each PO line. Without this every existing PO line
            // reads balance = orderQty (shippedQtyToDate stuck at the column default 0), under-counting shipped
            // history. Correlated UPDATE: AsnLine → Asn via asnId; exclude soft-deleted lines/ASNs and Cancelled
            // ASNs (asnStatus is the string enum name; ASN-cancel reversal already nets those to nil anyway).
            migrationBuilder.Sql(@"
UPDATE pol
SET [shippedQtyToDate] = ISNULL(x.totalShipped, 0)
FROM [proc].[PurchaseOrderLine] AS pol
OUTER APPLY (
    SELECT SUM(al.[shippedQty]) AS totalShipped
    FROM [proc].[AsnLine] AS al
    INNER JOIN [proc].[Asn] AS a ON a.[asnId] = al.[asnId]
    WHERE al.[purchaseOrderLineId] = pol.[purchaseOrderLineId]
      AND al.[isDeleted] = 0
      AND a.[isDeleted] = 0
      AND a.[asnStatus] <> 'Cancelled'
) AS x;");

            migrationBuilder.AddColumn<decimal>(
                name: "overShipTolerancePct",
                schema: "inv",
                table: "Item",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "SupplierItem",
                schema: "inv",
                columns: table => new
                {
                    supplierItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    itemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    overShipTolerancePct = table.Column<decimal>(type: "decimal(5,2)", nullable: true),
                    supplierItemSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SupplierItem", x => x.supplierItemId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SupplierItem_Item_itemId",
                        column: x => x.itemId,
                        principalSchema: "inv",
                        principalTable: "Item",
                        principalColumn: "itemId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierItem_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierItem_Supplier_supplierId",
                        column: x => x.supplierId,
                        principalSchema: "supplier",
                        principalTable: "Supplier",
                        principalColumn: "supplierId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierItem_itemId",
                schema: "inv",
                table: "SupplierItem",
                column: "itemId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierItem_seccodeId",
                schema: "inv",
                table: "SupplierItem",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierItem_tenant_company",
                schema: "inv",
                table: "SupplierItem",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_SupplierItem_supplier_item",
                schema: "inv",
                table: "SupplierItem",
                columns: new[] { "supplierId", "itemId" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierItem_supplierItemSeq",
                schema: "inv",
                table: "SupplierItem",
                column: "supplierItemSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierItem",
                schema: "inv");

            migrationBuilder.DropColumn(
                name: "allowNegotiate",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "allowReject",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "shippedQtyToDate",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "overShipTolerancePct",
                schema: "inv",
                table: "Item");
        }
    }
}
