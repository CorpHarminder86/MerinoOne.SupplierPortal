using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0028_PurchaseOrderLineUniqueKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderLine_purchaseOrderId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrderLine_po_position_seq",
                schema: "proc",
                table: "PurchaseOrderLine",
                columns: new[] { "purchaseOrderId", "positionNo", "sequenceNo" },
                unique: true,
                filter: "[isDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_PurchaseOrderLine_po_position_seq",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_purchaseOrderId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "purchaseOrderId");
        }
    }
}
