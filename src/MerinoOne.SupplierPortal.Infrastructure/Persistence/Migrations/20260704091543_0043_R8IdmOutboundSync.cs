using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0043_R8IdmOutboundSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "erpCompany",
                schema: "proc",
                table: "Invoice",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpDocumentNo",
                schema: "proc",
                table: "Invoice",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpTransactionType",
                schema: "proc",
                table: "Invoice",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "idmEntityType",
                schema: "doc",
                table: "DocumentUpload",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pid",
                schema: "doc",
                table: "DocumentUpload",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpCompany",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpDocumentNo",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "erpTransactionType",
                schema: "proc",
                table: "Asn",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdmAttachmentTypeConfig",
                schema: "integration",
                columns: table => new
                {
                    idmAttachmentTypeConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    attachmentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    idmEntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    eligibilityGateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    createMappingExpression = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    mutateMappingExpression = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    createMappingSeedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    mutateMappingSeedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    isEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    idmAttachmentTypeConfigSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_IdmAttachmentTypeConfig", x => x.idmAttachmentTypeConfigId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "IdmDocumentOutbox",
                schema: "integration",
                columns: table => new
                {
                    idmDocumentOutboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    documentUploadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    idmEntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ownerEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    fileName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    operation = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "nvarchar(14)", maxLength: 14, nullable: false, defaultValue: "Blocked"),
                    correlationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    externalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    attemptCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    nextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    requestSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    responseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    lastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    idmDocumentOutboxSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_IdmDocumentOutbox", x => x.idmDocumentOutboxId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_IdmDocumentOutbox_operation", "[operation] IN ('Create','Update','Delete')");
                    table.CheckConstraint("CK_IdmDocumentOutbox_status", "[status] IN ('Blocked','Pending','InFlight','Success','Failed','Unresolvable')");
                    table.ForeignKey(
                        name: "FK_IdmDocumentOutbox_DocumentUpload_DocumentUploadId",
                        column: x => x.documentUploadId,
                        principalSchema: "doc",
                        principalTable: "DocumentUpload",
                        principalColumn: "documentUploadId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdmDocumentOutbox_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutboundEndpointConfig",
                schema: "integration",
                columns: table => new
                {
                    outboundEndpointConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    targetSystem = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    endpointKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    httpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    relativePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    staticHeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ackParserKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    defaultAcl = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    isEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    outboundEndpointConfigSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_OutboundEndpointConfig", x => x.outboundEndpointConfigId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_OutboundEndpointConfig_httpMethod", "[httpMethod] IN ('POST','PUT','DELETE')");
                });

            migrationBuilder.CreateIndex(
                name: "UQ_IdmAttachmentTypeConfig_tenant_attachmentType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                columns: new[] { "tenantId", "attachmentType" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_IdmAttachmentTypeConfig_idmAttachmentTypeConfigSeq",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                column: "idmAttachmentTypeConfigSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_IdmDocumentOutbox_documentUploadId_seq",
                schema: "integration",
                table: "IdmDocumentOutbox",
                columns: new[] { "documentUploadId", "idmDocumentOutboxSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_IdmDocumentOutbox_seccodeId",
                schema: "integration",
                table: "IdmDocumentOutbox",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_IdmDocumentOutbox_status_nextAttemptAt",
                schema: "integration",
                table: "IdmDocumentOutbox",
                columns: new[] { "status", "nextAttemptAt" })
                .Annotation("SqlServer:Include", new[] { "documentUploadId", "idmDocumentOutboxSeq" });

            migrationBuilder.CreateIndex(
                name: "UQ_IdmDocumentOutbox_correlationId",
                schema: "integration",
                table: "IdmDocumentOutbox",
                column: "correlationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_IdmDocumentOutbox_idmDocumentOutboxSeq",
                schema: "integration",
                table: "IdmDocumentOutbox",
                column: "idmDocumentOutboxSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_OutboundEndpointConfig_tenant_endpointKey",
                schema: "integration",
                table: "OutboundEndpointConfig",
                columns: new[] { "tenantId", "endpointKey" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_OutboundEndpointConfig_outboundEndpointConfigSeq",
                schema: "integration",
                table: "OutboundEndpointConfig",
                column: "outboundEndpointConfigSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdmAttachmentTypeConfig",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "IdmDocumentOutbox",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "OutboundEndpointConfig",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "erpCompany",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "erpDocumentNo",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "erpTransactionType",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "idmEntityType",
                schema: "doc",
                table: "DocumentUpload");

            migrationBuilder.DropColumn(
                name: "pid",
                schema: "doc",
                table: "DocumentUpload");

            migrationBuilder.DropColumn(
                name: "erpCompany",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "erpDocumentNo",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropColumn(
                name: "erpTransactionType",
                schema: "proc",
                table: "Asn");
        }
    }
}
