using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0039_R5AsnApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "deliveryScheduleId",
                schema: "proc",
                table: "AsnLine",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "shipToAddressId",
                schema: "proc",
                table: "Asn",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AsnApproval",
                schema: "proc",
                columns: table => new
                {
                    asnApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    asnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    submittedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    submittedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    decisionBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    decisionOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    asnApprovalSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AsnApproval", x => x.asnApprovalId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AsnApproval_Asn_asnId",
                        column: x => x.asnId,
                        principalSchema: "proc",
                        principalTable: "Asn",
                        principalColumn: "asnId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AsnApproval_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AsnLine_deliveryScheduleId",
                schema: "proc",
                table: "AsnLine",
                column: "deliveryScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_Asn_shipTo",
                schema: "proc",
                table: "Asn",
                column: "shipToAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnApproval_asn",
                schema: "proc",
                table: "AsnApproval",
                column: "asnId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnApproval_seccodeId",
                schema: "proc",
                table: "AsnApproval",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnApproval_tenant_company",
                schema: "proc",
                table: "AsnApproval",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_AsnApproval_asnApprovalSeq",
                schema: "proc",
                table: "AsnApproval",
                column: "asnApprovalSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_Asn_CompanyAddress_shipToAddressId",
                schema: "proc",
                table: "Asn",
                column: "shipToAddressId",
                principalSchema: "admin",
                principalTable: "CompanyAddress",
                principalColumn: "companyAddressId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AsnLine_DeliverySchedule_deliveryScheduleId",
                schema: "proc",
                table: "AsnLine",
                column: "deliveryScheduleId",
                principalSchema: "proc",
                principalTable: "DeliverySchedule",
                principalColumn: "deliveryScheduleId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Asn_CompanyAddress_shipToAddressId",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropForeignKey(
                name: "FK_AsnLine_DeliverySchedule_deliveryScheduleId",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropTable(
                name: "AsnApproval",
                schema: "proc");

            migrationBuilder.DropIndex(
                name: "IX_AsnLine_deliveryScheduleId",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropIndex(
                name: "IX_Asn_shipTo",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "deliveryScheduleId",
                schema: "proc",
                table: "AsnLine");

            migrationBuilder.DropColumn(
                name: "shipToAddressId",
                schema: "proc",
                table: "Asn");
        }
    }
}
