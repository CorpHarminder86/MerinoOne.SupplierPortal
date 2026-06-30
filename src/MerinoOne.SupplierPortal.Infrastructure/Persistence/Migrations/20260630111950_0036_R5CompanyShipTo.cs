using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0036_R5CompanyShipTo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "erpStatus",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "shipToAddressId",
                schema: "proc",
                table: "PurchaseOrder",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToAddressName",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToCity",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToCountry",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToErpCode",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToLine1",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToLine2",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToPincode",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipToState",
                schema: "proc",
                table: "PurchaseOrder",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Company",
                schema: "admin",
                columns: table => new
                {
                    companyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    companySeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Company", x => x.companyId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Company_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyAddress",
                schema: "admin",
                columns: table => new
                {
                    companyAddressId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    companyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    addressName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    erpCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    addressType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    addressLine1 = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    addressLine2 = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    city = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    pincode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "India"),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    companyAddressSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_CompanyAddress", x => x.companyAddressId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_CompanyAddress_Company_companyId",
                        column: x => x.companyId,
                        principalSchema: "admin",
                        principalTable: "Company",
                        principalColumn: "companyId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_shipTo",
                schema: "proc",
                table: "PurchaseOrder",
                column: "shipToAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_Company_seccodeId",
                schema: "admin",
                table: "Company",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "UQ_Company_tenant_entity",
                schema: "admin",
                table: "Company",
                columns: new[] { "tenantId", "tenantEntityId" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Company_companySeq",
                schema: "admin",
                table: "Company",
                column: "companySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyAddress_company",
                schema: "admin",
                table: "CompanyAddress",
                column: "companyId");

            migrationBuilder.CreateIndex(
                name: "UQ_CompanyAddress_company_erp",
                schema: "admin",
                table: "CompanyAddress",
                columns: new[] { "companyId", "erpCode" },
                unique: true,
                filter: "[erpCode] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_CompanyAddress_companyAddressSeq",
                schema: "admin",
                table: "CompanyAddress",
                column: "companyAddressSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrder_CompanyAddress_shipToAddressId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "shipToAddressId",
                principalSchema: "admin",
                principalTable: "CompanyAddress",
                principalColumn: "companyAddressId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrder_CompanyAddress_shipToAddressId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropTable(
                name: "CompanyAddress",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Company",
                schema: "admin");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrder_shipTo",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "erpStatus",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToAddressId",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToAddressName",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToCity",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToCountry",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToErpCode",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToLine1",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToLine2",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToPincode",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "shipToState",
                schema: "proc",
                table: "PurchaseOrder");
        }
    }
}
