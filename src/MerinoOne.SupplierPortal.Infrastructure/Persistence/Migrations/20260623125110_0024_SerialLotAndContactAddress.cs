using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0024_SerialLotAndContactAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "addressId",
                schema: "supplier",
                table: "SupplierContact",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AsnLineLot",
                schema: "proc",
                columns: table => new
                {
                    asnLineLotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    asnLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    lotNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    qty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    expiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    erpCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    asnLineLotSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AsnLineLot", x => x.asnLineLotId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AsnLineLot_AsnLine_AsnLineId",
                        column: x => x.asnLineId,
                        principalSchema: "proc",
                        principalTable: "AsnLine",
                        principalColumn: "asnLineId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AsnLineSerial",
                schema: "proc",
                columns: table => new
                {
                    asnLineSerialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    asnLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    serialNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    erpCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    asnLineSerialSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AsnLineSerial", x => x.asnLineSerialId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AsnLineSerial_AsnLine_AsnLineId",
                        column: x => x.asnLineId,
                        principalSchema: "proc",
                        principalTable: "AsnLine",
                        principalColumn: "asnLineId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierContact_addressId",
                schema: "supplier",
                table: "SupplierContact",
                column: "addressId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnLineLot_asnLineId",
                schema: "proc",
                table: "AsnLineLot",
                column: "asnLineId");

            migrationBuilder.CreateIndex(
                name: "UQ_AsnLineLot_asnLine_lot",
                schema: "proc",
                table: "AsnLineLot",
                columns: new[] { "asnLineId", "lotNo" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_AsnLineLot_asnLineLotSeq",
                schema: "proc",
                table: "AsnLineLot",
                column: "asnLineLotSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AsnLineSerial_asnLineId",
                schema: "proc",
                table: "AsnLineSerial",
                column: "asnLineId");

            migrationBuilder.CreateIndex(
                name: "UQ_AsnLineSerial_asnLine_serial",
                schema: "proc",
                table: "AsnLineSerial",
                columns: new[] { "asnLineId", "serialNumber" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_AsnLineSerial_asnLineSerialSeq",
                schema: "proc",
                table: "AsnLineSerial",
                column: "asnLineSerialSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierContact_SupplierAddress_addressId",
                schema: "supplier",
                table: "SupplierContact",
                column: "addressId",
                principalSchema: "supplier",
                principalTable: "SupplierAddress",
                principalColumn: "supplierAddressId");

            // R4 (2026-06-23) — Item XOR guard: serialized and lot-controlled are mutually exclusive per item.
            // Raw SQL (not EF HasCheckConstraint) so it's owned by this migration. NOTE: bracket [inv].[Item]
            // per the reserved-word bracketing convention. Existing rows default both flags to 0, so no
            // current row can violate NOT (1 AND 1).
            migrationBuilder.Sql(
                "ALTER TABLE [inv].[Item] ADD CONSTRAINT [CK_Item_serialized_xor_lot] " +
                "CHECK (NOT ([isSerialized] = 1 AND [isLotControlled] = 1));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE [inv].[Item] DROP CONSTRAINT [CK_Item_serialized_xor_lot];");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierContact_SupplierAddress_addressId",
                schema: "supplier",
                table: "SupplierContact");

            migrationBuilder.DropTable(
                name: "AsnLineLot",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "AsnLineSerial",
                schema: "proc");

            migrationBuilder.DropIndex(
                name: "IX_SupplierContact_addressId",
                schema: "supplier",
                table: "SupplierContact");

            migrationBuilder.DropColumn(
                name: "addressId",
                schema: "supplier",
                table: "SupplierContact");
        }
    }
}
