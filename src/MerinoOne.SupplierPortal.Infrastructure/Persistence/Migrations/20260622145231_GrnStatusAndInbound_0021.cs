using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrnStatusAndInbound_0021 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "erpCode",
                schema: "proc",
                table: "GoodsReceipt",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "grnApprovedAt",
                schema: "proc",
                table: "GoodsReceipt",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "grnStatus",
                schema: "proc",
                table: "GoodsReceipt",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "GrnNotApproved");

            migrationBuilder.AddColumn<Guid>(
                name: "invoiceId",
                schema: "proc",
                table: "GoodsReceipt",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "issueReported",
                schema: "proc",
                table: "GoodsReceipt",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_grnStatus",
                schema: "proc",
                table: "GoodsReceipt",
                column: "grnStatus",
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_invoiceId",
                schema: "proc",
                table: "GoodsReceipt",
                column: "invoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_tenant_company",
                schema: "proc",
                table: "GoodsReceipt",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.AddForeignKey(
                name: "FK_GoodsReceipt_Invoice_InvoiceId",
                schema: "proc",
                table: "GoodsReceipt",
                column: "invoiceId",
                principalSchema: "proc",
                principalTable: "Invoice",
                principalColumn: "invoiceId",
                onDelete: ReferentialAction.Restrict);

            // R4 (2026-06-22) — Module 5 / Increment D (schema finding 16). Backfill ALL existing GRNs to
            // 'GrnNotApproved' (NOT the brittle Invoice.GrnReference heuristic). invoiceId is NULL at migration
            // time — the deterministic GRN→Invoice link is populated later by the inbound cascade, and Live LN
            // inbound flips the status. The NOT NULL column default already stamps existing rows; this explicit,
            // idempotent UPDATE documents the all-GRNs intent and is safe if the default is ever removed.
            // NOTE: 'proc' is a T-SQL reserved word (abbreviates PROCEDURE) so the schema MUST be bracketed.
            migrationBuilder.Sql(@"
UPDATE [proc].[GoodsReceipt]
   SET grnStatus = 'GrnNotApproved'
 WHERE grnStatus IS NULL
    OR grnStatus = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GoodsReceipt_Invoice_InvoiceId",
                schema: "proc",
                table: "GoodsReceipt");

            migrationBuilder.DropIndex(
                name: "IX_GoodsReceipt_grnStatus",
                schema: "proc",
                table: "GoodsReceipt");

            migrationBuilder.DropIndex(
                name: "IX_GoodsReceipt_invoiceId",
                schema: "proc",
                table: "GoodsReceipt");

            migrationBuilder.DropIndex(
                name: "IX_GoodsReceipt_tenant_company",
                schema: "proc",
                table: "GoodsReceipt");

            migrationBuilder.DropColumn(
                name: "erpCode",
                schema: "proc",
                table: "GoodsReceipt");

            migrationBuilder.DropColumn(
                name: "grnApprovedAt",
                schema: "proc",
                table: "GoodsReceipt");

            migrationBuilder.DropColumn(
                name: "grnStatus",
                schema: "proc",
                table: "GoodsReceipt");

            migrationBuilder.DropColumn(
                name: "invoiceId",
                schema: "proc",
                table: "GoodsReceipt");

            migrationBuilder.DropColumn(
                name: "issueReported",
                schema: "proc",
                table: "GoodsReceipt");
        }
    }
}
