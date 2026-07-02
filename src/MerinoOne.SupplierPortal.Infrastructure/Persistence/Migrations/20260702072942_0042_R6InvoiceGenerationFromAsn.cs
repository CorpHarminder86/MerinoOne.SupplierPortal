using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0042_R6InvoiceGenerationFromAsn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_Invoice_asnId",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.AddColumn<bool>(
                name: "isRateOverridden",
                schema: "proc",
                table: "Tax",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "lastSyncedRate",
                schema: "proc",
                table: "Tax",
                type: "decimal(9,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "invoicedQtyToDate",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "taxDescription",
                schema: "proc",
                table: "InvoiceLine",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "taxId",
                schema: "proc",
                table: "InvoiceLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "taxRatePct",
                schema: "proc",
                table: "InvoiceLine",
                type: "decimal(9,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoiceOrigin",
                schema: "proc",
                table: "Invoice",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "SupplierManual");

            migrationBuilder.AddColumn<string>(
                name: "invoiceGenerationNote",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoiceGenerationStatus",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            // [[r6]] — provenance backfill: every existing ASN-linked invoice was auto-drafted by the R5
            // generator; everything else stays at the column default 'SupplierManual'.
            migrationBuilder.Sql(@"
                UPDATE [proc].[Invoice] SET invoiceOrigin = 'AsnGenerated' WHERE asnId IS NOT NULL;");

            // [[r6]] — lastSyncedRate seeds from the current rate: every rate to date came from sync/seed
            // (no override UI existed before R6), so 'latest inbound value' == current taxRate.
            migrationBuilder.Sql(@"
                UPDATE tl SET tl.lastSyncedRate = tl.taxRate FROM [proc].[Tax] AS tl;");

            // [[r6]] — invoicedQtyToDate backfill: Σ billedQty per PO line over reservation-holding invoices.
            // Deliberately EXCLUDES Draft/Rejected/Cancelled — the runtime reservation-release invariant
            // (plan D8: revoke/reject give the quantity back). Lines with no qualifying invoice keep the
            // column default 0.
            migrationBuilder.Sql(@"
                UPDATE pol SET pol.invoicedQtyToDate = agg.total
                FROM [proc].[PurchaseOrderLine] AS pol
                INNER JOIN (
                    SELECT il.purchaseOrderLineId, SUM(il.billedQty) AS total
                    FROM [proc].[InvoiceLine] AS il
                    INNER JOIN [proc].[Invoice] AS i ON i.invoiceId = il.invoiceId
                    WHERE i.isDeleted = 0 AND il.isDeleted = 0
                      AND i.invoiceStatus NOT IN ('Draft','Rejected','Cancelled')
                    GROUP BY il.purchaseOrderLineId
                ) AS agg ON agg.purchaseOrderLineId = pol.purchaseOrderLineId;");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_taxId",
                schema: "proc",
                table: "InvoiceLine",
                column: "taxId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_asnId",
                schema: "proc",
                table: "Invoice",
                column: "asnId");

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceLine_Tax_TaxId",
                schema: "proc",
                table: "InvoiceLine",
                column: "taxId",
                principalSchema: "proc",
                principalTable: "Tax",
                principalColumn: "taxId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceLine_Tax_TaxId",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLine_taxId",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropIndex(
                name: "IX_Invoice_asnId",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "isRateOverridden",
                schema: "proc",
                table: "Tax");

            migrationBuilder.DropColumn(
                name: "lastSyncedRate",
                schema: "proc",
                table: "Tax");

            migrationBuilder.DropColumn(
                name: "invoicedQtyToDate",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "taxDescription",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "taxId",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "taxRatePct",
                schema: "proc",
                table: "InvoiceLine");

            migrationBuilder.DropColumn(
                name: "invoiceOrigin",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "invoiceGenerationNote",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "invoiceGenerationStatus",
                schema: "proc",
                table: "Asn");

            migrationBuilder.CreateIndex(
                name: "UQ_Invoice_asnId",
                schema: "proc",
                table: "Invoice",
                column: "asnId",
                unique: true,
                filter: "[asnId] IS NOT NULL AND [isDeleted] = 0");
        }
    }
}
