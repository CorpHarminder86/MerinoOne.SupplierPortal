using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0029_PoNegotiation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseOrderNegotiation",
                schema: "proc",
                columns: table => new
                {
                    purchaseOrderNegotiationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    purchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    poNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    negotiationStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    previousPoStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    submittedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    reviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    reviewedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    rejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    purchaseOrderNegotiationSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_PurchaseOrderNegotiation", x => x.purchaseOrderNegotiationId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderNegotiation_PurchaseOrder_PurchaseOrderId",
                        column: x => x.purchaseOrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "purchaseOrderId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderNegotiation_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderNegotiationLine",
                schema: "proc",
                columns: table => new
                {
                    purchaseOrderNegotiationLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    purchaseOrderNegotiationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    purchaseOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    positionNo = table.Column<int>(type: "int", nullable: false),
                    sequenceNo = table.Column<int>(type: "int", nullable: false),
                    itemCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    originalQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    negotiatedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    originalDeliveryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    negotiatedDeliveryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    purchaseOrderNegotiationLineSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_PurchaseOrderNegotiationLine", x => x.purchaseOrderNegotiationLineId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderNegotiationLine_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.purchaseOrderLineId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "purchaseOrderLineId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderNegotiationLine_PurchaseOrderNegotiation_PurchaseOrderNegotiationId",
                        column: x => x.purchaseOrderNegotiationId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrderNegotiation",
                        principalColumn: "purchaseOrderNegotiationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderNegotiation_negotiationStatus",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                column: "negotiationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderNegotiation_seccodeId",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderNegotiation_tenant_company",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrderNegotiation_po_open",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                column: "purchaseOrderId",
                unique: true,
                filter: "[negotiationStatus] = 'Submitted' AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrderNegotiation_purchaseOrderNegotiationSeq",
                schema: "proc",
                table: "PurchaseOrderNegotiation",
                column: "purchaseOrderNegotiationSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderNegotiationLine_negotiation",
                schema: "proc",
                table: "PurchaseOrderNegotiationLine",
                column: "purchaseOrderNegotiationId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderNegotiationLine_purchaseOrderLineId",
                schema: "proc",
                table: "PurchaseOrderNegotiationLine",
                column: "purchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrderNegotiationLine_purchaseOrderNegotiationLineSeq",
                schema: "proc",
                table: "PurchaseOrderNegotiationLine",
                column: "purchaseOrderNegotiationLineSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseOrderNegotiationLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "PurchaseOrderNegotiation",
                schema: "proc");
        }
    }
}
