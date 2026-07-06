using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0048_R9GateBackfillSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "errorClass",
                schema: "integration",
                table: "OutboxMessage",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HeldInboundMessage",
                schema: "integration",
                columns: table => new
                {
                    heldInboundMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    endpointName = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false, defaultValue: "ErpAck"),
                    payloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    idempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    boundCompanyIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "Held"),
                    replayAttempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    replayedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    lastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    heldInboundMessageSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_HeldInboundMessage", x => x.heldInboundMessageId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_HeldInboundMessage_status", "[status] IN ('Held','Replayed','Failed')");
                });

            migrationBuilder.CreateTable(
                name: "IntegrationSwitch",
                schema: "integration",
                columns: table => new
                {
                    integrationSwitchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    isEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    lastReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    integrationSwitchSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_IntegrationSwitch", x => x.integrationSwitchId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_IntegrationSwitch_scope", "[scope] IN ('OutboundGlobal','InboundErpAck')");
                });

            migrationBuilder.CreateTable(
                name: "LnBackfillRun",
                schema: "integration",
                columns: table => new
                {
                    lnBackfillRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    lnEndpointConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    transactionType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    gateVersion = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false, defaultValue: "DryRun"),
                    enqueueCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    rearmCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    withdrawCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    dryRunResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    appliedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    appliedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    applyResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    lnBackfillRunSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_LnBackfillRun", x => x.lnBackfillRunId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_LnBackfillRun_status", "[status] IN ('DryRun','Applied','Superseded','Discarded')");
                    table.ForeignKey(
                        name: "FK_LnBackfillRun_LnEndpointConfig_LnEndpointConfigId",
                        column: x => x.lnEndpointConfigId,
                        principalSchema: "integration",
                        principalTable: "LnEndpointConfig",
                        principalColumn: "lnEndpointConfigId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationSwitchAudit",
                schema: "integration",
                columns: table => new
                {
                    integrationSwitchAuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    integrationSwitchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    scope = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    oldEnabled = table.Column<bool>(type: "bit", nullable: false),
                    newEnabled = table.Column<bool>(type: "bit", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    integrationSwitchAuditSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_IntegrationSwitchAudit", x => x.integrationSwitchAuditId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_IntegrationSwitchAudit_IntegrationSwitch_IntegrationSwitchId",
                        column: x => x.integrationSwitchId,
                        principalSchema: "integration",
                        principalTable: "IntegrationSwitch",
                        principalColumn: "integrationSwitchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutboxMessage_errorClass",
                schema: "integration",
                table: "OutboxMessage",
                sql: "[errorClass] IS NULL OR [errorClass] IN ('Permanent','Retriable')");

            migrationBuilder.CreateIndex(
                name: "IX_HeldInboundMessage_tenant_status",
                schema: "integration",
                table: "HeldInboundMessage",
                columns: new[] { "tenantId", "status" },
                filter: "[status] = 'Held' AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_HeldInboundMessage_heldInboundMessageSeq",
                schema: "integration",
                table: "HeldInboundMessage",
                column: "heldInboundMessageSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_IntegrationSwitch_tenant_scope",
                schema: "integration",
                table: "IntegrationSwitch",
                columns: new[] { "tenantId", "scope" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_IntegrationSwitch_integrationSwitchSeq",
                schema: "integration",
                table: "IntegrationSwitch",
                column: "integrationSwitchSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationSwitchAudit_switch",
                schema: "integration",
                table: "IntegrationSwitchAudit",
                column: "integrationSwitchId");

            migrationBuilder.CreateIndex(
                name: "UX_IntegrationSwitchAudit_integrationSwitchAuditSeq",
                schema: "integration",
                table: "IntegrationSwitchAudit",
                column: "integrationSwitchAuditSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_LnBackfillRun_config_status",
                schema: "integration",
                table: "LnBackfillRun",
                columns: new[] { "lnEndpointConfigId", "status" },
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_LnBackfillRun_lnBackfillRunSeq",
                schema: "integration",
                table: "LnBackfillRun",
                column: "lnBackfillRunSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HeldInboundMessage",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "IntegrationSwitchAudit",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "LnBackfillRun",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "IntegrationSwitch",
                schema: "integration");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OutboxMessage_errorClass",
                schema: "integration",
                table: "OutboxMessage");

            migrationBuilder.DropColumn(
                name: "errorClass",
                schema: "integration",
                table: "OutboxMessage");
        }
    }
}
