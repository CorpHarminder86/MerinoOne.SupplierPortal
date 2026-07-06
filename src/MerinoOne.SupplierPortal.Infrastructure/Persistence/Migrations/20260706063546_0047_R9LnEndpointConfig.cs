using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0047_R9LnEndpointConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "gateVersion",
                schema: "integration",
                table: "OutboxMessage",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sequenceInstanceStepId",
                schema: "integration",
                table: "OutboxMessage",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "skipReason",
                schema: "integration",
                table: "OutboxMessage",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LnEndpointConfig",
                schema: "integration",
                columns: table => new
                {
                    lnEndpointConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    transactionType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    portalEntity = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    endpointPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    httpVerb = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "POST"),
                    dispatchMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Legacy"),
                    eligibilityGateExpr = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    requestMappingExpr = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    responseMappingExpr = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ackMappingExpr = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    requestMappingSeedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    responseMappingSeedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ackMappingSeedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    candidateFilterName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    candidateFilterParams = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    gateVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    sampleDocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    sampleBuilderVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    responseSampleJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ackSampleJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    verifiedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    verifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    verifiedNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    pathConfirmed = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    lnEndpointConfigSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_LnEndpointConfig", x => x.lnEndpointConfigId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_LnEndpointConfig_dispatchMode", "[dispatchMode] IN ('Legacy','Dynamic','Held')");
                    table.CheckConstraint("CK_LnEndpointConfig_httpVerb", "[httpVerb] IN ('POST','PUT','PATCH')");
                });

            migrationBuilder.CreateIndex(
                name: "UQ_LnEndpointConfig_tenant_transactionType",
                schema: "integration",
                table: "LnEndpointConfig",
                columns: new[] { "tenantId", "transactionType" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_LnEndpointConfig_lnEndpointConfigSeq",
                schema: "integration",
                table: "LnEndpointConfig",
                column: "lnEndpointConfigSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LnEndpointConfig",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "gateVersion",
                schema: "integration",
                table: "OutboxMessage");

            migrationBuilder.DropColumn(
                name: "sequenceInstanceStepId",
                schema: "integration",
                table: "OutboxMessage");

            migrationBuilder.DropColumn(
                name: "skipReason",
                schema: "integration",
                table: "OutboxMessage");
        }
    }
}
