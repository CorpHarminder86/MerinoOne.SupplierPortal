using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AsnMultiPoAndSubmit_0019 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "positionNo",
                schema: "proc",
                table: "AsnLine",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sequenceNo",
                schema: "proc",
                table: "AsnLine",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "purchaseOrderId",
                schema: "proc",
                table: "Asn",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "erpCode",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpSyncId",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "submittedAt",
                schema: "proc",
                table: "Asn",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submittedBy",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AsnPurchaseOrder",
                schema: "proc",
                columns: table => new
                {
                    asnPurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    asnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    purchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    asnPurchaseOrderSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AsnPurchaseOrder", x => x.asnPurchaseOrderId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AsnPurchaseOrder_Asn_AsnId",
                        column: x => x.asnId,
                        principalSchema: "proc",
                        principalTable: "Asn",
                        principalColumn: "asnId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AsnPurchaseOrder_PurchaseOrder_PurchaseOrderId",
                        column: x => x.purchaseOrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "purchaseOrderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AsnPurchaseOrder_asnId",
                schema: "proc",
                table: "AsnPurchaseOrder",
                column: "asnId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnPurchaseOrder_purchaseOrderId",
                schema: "proc",
                table: "AsnPurchaseOrder",
                column: "purchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "UQ_AsnPurchaseOrder_asn_po",
                schema: "proc",
                table: "AsnPurchaseOrder",
                columns: new[] { "asnId", "purchaseOrderId" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_AsnPurchaseOrder_asnPurchaseOrderSeq",
                schema: "proc",
                table: "AsnPurchaseOrder",
                column: "asnPurchaseOrderSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            // R4 (2026-06-22) — Addendum A4 backfill: snapshot positionNo/sequenceNo onto every existing
            // ASN line from its source PO line. New lines copy these at creation (backend, Increment B).
            // NOTE: 'proc' is a T-SQL reserved word (abbreviates PROCEDURE) so the schema MUST be bracketed.
            migrationBuilder.Sql(@"
UPDATE al
   SET al.positionNo = pol.positionNo,
       al.sequenceNo = pol.sequenceNo
  FROM [proc].[AsnLine] AS al
  INNER JOIN [proc].[PurchaseOrderLine] AS pol
          ON al.purchaseOrderLineId = pol.purchaseOrderLineId;");

            // R4 (2026-06-22) — Module 3 backfill: existing ASNs that are already 'Submitted' (the old entity
            // default) get submittedAt = createdOn so the new draft/submit lifecycle has a sane timestamp.
            migrationBuilder.Sql(@"
UPDATE [proc].[Asn]
   SET submittedAt = createdOn
 WHERE asnStatus = 'Submitted'
   AND submittedAt IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AsnPurchaseOrder",
                schema: "proc");

            migrationBuilder.DropColumn(
                name: "positionNo",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropColumn(
                name: "sequenceNo",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropColumn(
                name: "erpCode",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "erpSyncId",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "submittedAt",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "submittedBy",
                schema: "proc",
                table: "Asn");

            migrationBuilder.AlterColumn<Guid>(
                name: "purchaseOrderId",
                schema: "proc",
                table: "Asn",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
