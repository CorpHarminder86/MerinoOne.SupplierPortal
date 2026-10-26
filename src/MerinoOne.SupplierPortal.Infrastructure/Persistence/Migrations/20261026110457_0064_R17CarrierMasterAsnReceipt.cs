using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0064_R17CarrierMasterAsnReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "actualDeliveryDate",
                schema: "proc",
                table: "AsnLine",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "receivedQty",
                schema: "proc",
                table: "AsnLine",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "carrierCode",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpShipmentStatus",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Carrier",
                schema: "proc",
                columns: table => new
                {
                    carrierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: false),
                    description = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    carrierSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Carrier", x => x.carrierId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Carrier_tenant_company",
                schema: "proc",
                table: "Carrier",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_Carrier_company_code",
                schema: "proc",
                table: "Carrier",
                columns: new[] { "tenantEntityId", "code" },
                unique: true,
                filter: "[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Carrier_carrierSeq",
                schema: "proc",
                table: "Carrier",
                column: "carrierSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Carrier",
                schema: "proc");

            migrationBuilder.DropColumn(
                name: "actualDeliveryDate",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropColumn(
                name: "receivedQty",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropColumn(
                name: "carrierCode",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "erpShipmentStatus",
                schema: "proc",
                table: "Asn");
        }
    }
}
