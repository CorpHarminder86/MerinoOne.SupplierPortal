using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplierChangeRequest_0018 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierChangeRequest",
                schema: "supplier",
                columns: table => new
                {
                    supplierChangeRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    changeStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    requestedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    requestedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    reviewedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    reviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    rejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    supplierChangeRequestSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SupplierChangeRequest", x => x.supplierChangeRequestId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SupplierChangeRequest_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierChangeRequest_Supplier_SupplierId",
                        column: x => x.supplierId,
                        principalSchema: "supplier",
                        principalTable: "Supplier",
                        principalColumn: "supplierId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierChangeRequestLine",
                schema: "supplier",
                columns: table => new
                {
                    supplierChangeRequestLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierChangeRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    targetEntity = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    targetEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    operation = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    fieldName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    oldValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    newValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    payloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    pushStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    pushedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    erpRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    supplierChangeRequestLineSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SupplierChangeRequestLine", x => x.supplierChangeRequestLineId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SupplierChangeRequestLine_SupplierChangeRequest_SupplierChangeRequestId",
                        column: x => x.supplierChangeRequestId,
                        principalSchema: "supplier",
                        principalTable: "SupplierChangeRequest",
                        principalColumn: "supplierChangeRequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierChangeRequest_seccodeId",
                schema: "supplier",
                table: "SupplierChangeRequest",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierChangeRequest_supplier_status",
                schema: "supplier",
                table: "SupplierChangeRequest",
                columns: new[] { "supplierId", "changeStatus" });

            migrationBuilder.CreateIndex(
                name: "UX_SupplierChangeRequest_supplierChangeRequestSeq",
                schema: "supplier",
                table: "SupplierChangeRequest",
                column: "supplierChangeRequestSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierChangeRequestLine_request",
                schema: "supplier",
                table: "SupplierChangeRequestLine",
                column: "supplierChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierChangeRequestLine_supplierChangeRequestLineSeq",
                schema: "supplier",
                table: "SupplierChangeRequestLine",
                column: "supplierChangeRequestLineSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierChangeRequestLine",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "SupplierChangeRequest",
                schema: "supplier");
        }
    }
}
