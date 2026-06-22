using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvoicePostingLifecycle_0020 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoice_asnId",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.AlterColumn<Guid>(
                name: "purchaseOrderId",
                schema: "proc",
                table: "Invoice",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "erpCode",
                schema: "proc",
                table: "Invoice",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "erpPostedAt",
                schema: "proc",
                table: "Invoice",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpSyncId",
                schema: "proc",
                table: "Invoice",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revokeReason",
                schema: "proc",
                table: "Invoice",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "revokedAt",
                schema: "proc",
                table: "Invoice",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "revokedBy",
                schema: "proc",
                table: "Invoice",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "submittedAt",
                schema: "proc",
                table: "Invoice",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Invoice_asnId",
                schema: "proc",
                table: "Invoice",
                column: "asnId",
                unique: true,
                filter: "[asnId] IS NOT NULL AND [isDeleted] = 0");

            // R4 (2026-06-22) — Module 4 backfill: existing invoices past Draft (the old 'Submitted' default
            // and beyond) get submittedAt = createdOn so the new posting lifecycle has a sane timestamp.
            // NOTE: 'proc' is a T-SQL reserved word (abbreviates PROCEDURE) so the schema MUST be bracketed.
            migrationBuilder.Sql(@"
UPDATE [proc].[Invoice]
   SET submittedAt = createdOn
 WHERE invoiceStatus <> 'Draft'
   AND submittedAt IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_Invoice_asnId",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "erpCode",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "erpPostedAt",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "erpSyncId",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "revokeReason",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "revokedAt",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "revokedBy",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "submittedAt",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.AlterColumn<Guid>(
                name: "purchaseOrderId",
                schema: "proc",
                table: "Invoice",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_asnId",
                schema: "proc",
                table: "Invoice",
                column: "asnId");
        }
    }
}
