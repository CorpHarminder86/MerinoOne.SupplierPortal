using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0032_AttachmentGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttachmentEntity",
                schema: "doc",
                columns: table => new
                {
                    attachmentEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    attachmentEntitySeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AttachmentEntity", x => x.attachmentEntityId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AttachmentEntity_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttachmentType",
                schema: "doc",
                columns: table => new
                {
                    attachmentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    attachmentTypeSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AttachmentType", x => x.attachmentTypeId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AttachmentType_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttachmentRequirementPolicy",
                schema: "doc",
                columns: table => new
                {
                    attachmentRequirementPolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    attachmentEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    attachmentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    requirement = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    attachmentRequirementPolicySeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AttachmentRequirementPolicy", x => x.attachmentRequirementPolicyId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AttachmentRequirementPolicy_AttachmentEntity_attachmentEntityId",
                        column: x => x.attachmentEntityId,
                        principalSchema: "doc",
                        principalTable: "AttachmentEntity",
                        principalColumn: "attachmentEntityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttachmentRequirementPolicy_AttachmentType_attachmentTypeId",
                        column: x => x.attachmentTypeId,
                        principalSchema: "doc",
                        principalTable: "AttachmentType",
                        principalColumn: "attachmentTypeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttachmentRequirementPolicy_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AttachmentRequirementPolicy_Supplier_supplierId",
                        column: x => x.supplierId,
                        principalSchema: "supplier",
                        principalTable: "Supplier",
                        principalColumn: "supplierId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentEntity_seccodeId",
                schema: "doc",
                table: "AttachmentEntity",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentEntity_tenant_company",
                schema: "doc",
                table: "AttachmentEntity",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_AttachmentEntity_tenant_code",
                schema: "doc",
                table: "AttachmentEntity",
                columns: new[] { "tenantId", "code" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_AttachmentEntity_attachmentEntitySeq",
                schema: "doc",
                table: "AttachmentEntity",
                column: "attachmentEntitySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRequirementPolicy_attachmentEntityId",
                schema: "doc",
                table: "AttachmentRequirementPolicy",
                column: "attachmentEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRequirementPolicy_attachmentTypeId",
                schema: "doc",
                table: "AttachmentRequirementPolicy",
                column: "attachmentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRequirementPolicy_seccodeId",
                schema: "doc",
                table: "AttachmentRequirementPolicy",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRequirementPolicy_supplierId",
                schema: "doc",
                table: "AttachmentRequirementPolicy",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentRequirementPolicy_tenant_company",
                schema: "doc",
                table: "AttachmentRequirementPolicy",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_ARP_supplier_override",
                schema: "doc",
                table: "AttachmentRequirementPolicy",
                columns: new[] { "tenantId", "supplierId", "attachmentEntityId", "attachmentTypeId" },
                unique: true,
                filter: "[supplierId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_ARP_tenant_default",
                schema: "doc",
                table: "AttachmentRequirementPolicy",
                columns: new[] { "tenantId", "attachmentEntityId", "attachmentTypeId" },
                unique: true,
                filter: "[supplierId] IS NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_AttachmentRequirementPolicy_attachmentRequirementPolicySeq",
                schema: "doc",
                table: "AttachmentRequirementPolicy",
                column: "attachmentRequirementPolicySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentType_seccodeId",
                schema: "doc",
                table: "AttachmentType",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentType_tenant_company",
                schema: "doc",
                table: "AttachmentType",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_AttachmentType_tenant_code",
                schema: "doc",
                table: "AttachmentType",
                columns: new[] { "tenantId", "code" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_AttachmentType_attachmentTypeSeq",
                schema: "doc",
                table: "AttachmentType",
                column: "attachmentTypeSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachmentRequirementPolicy",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "AttachmentEntity",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "AttachmentType",
                schema: "doc");
        }
    }
}
