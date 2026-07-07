using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0051_R10OutboundIntegrationUnification : Migration
    {
        // R10 — HAND-SHAPED (the scaffold produced drop/create, losing data). This migration:
        //   1. renames integration.LnEndpointConfig → integration.OutboundIntegrationConfig IN PLACE
        //      (columns, PK, clustered UX, CHECKs renamed; the two per-kind filtered UQs replace the old one);
        //   2. widens it into the unified config plane (kind + connection + document-routing + format columns);
        //   3. creates integration.ConnectionPoint;
        //   4. CONVERTS the R8 IDM config pair into Document-kind rows (one per IdmAttachmentTypeConfig row,
        //      joined to the tenant's IDM.Item.Create/Update/Delete transport rows for verb/path/headers and
        //      the acl/entityName context; dispatchMode maps the ATTACHMENT row's isEnabled — the endpoint
        //      row's isEnabled was Live-only and is not honored in Mock, so the attachment flag is the
        //      behavior-preserving source);
        //   5. drops the two R8 tables.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LnBackfillRun_LnEndpointConfig_LnEndpointConfigId",
                schema: "integration",
                table: "LnBackfillRun");

            // ── 1. Rename the R9 table + its identity objects in place (data preserved). ─────────────────
            migrationBuilder.DropIndex(
                name: "UQ_LnEndpointConfig_tenant_transactionType",
                schema: "integration",
                table: "LnEndpointConfig");

            migrationBuilder.RenameTable(
                name: "LnEndpointConfig", schema: "integration",
                newName: "OutboundIntegrationConfig", newSchema: "integration");
            migrationBuilder.RenameColumn(
                name: "lnEndpointConfigId", schema: "integration", table: "OutboundIntegrationConfig",
                newName: "outboundIntegrationConfigId");
            migrationBuilder.RenameColumn(
                name: "lnEndpointConfigSeq", schema: "integration", table: "OutboundIntegrationConfig",
                newName: "outboundIntegrationConfigSeq");
            migrationBuilder.RenameIndex(
                name: "UX_LnEndpointConfig_lnEndpointConfigSeq", schema: "integration", table: "OutboundIntegrationConfig",
                newName: "UX_OutboundIntegrationConfig_outboundIntegrationConfigSeq");
            migrationBuilder.Sql("EXEC sp_rename N'[integration].[PK_LnEndpointConfig]', N'PK_OutboundIntegrationConfig';");
            migrationBuilder.Sql("EXEC sp_rename N'[integration].[CK_LnEndpointConfig_httpVerb]', N'CK_OutboundIntegrationConfig_httpVerb';");
            migrationBuilder.Sql("EXEC sp_rename N'[integration].[CK_LnEndpointConfig_dispatchMode]', N'CK_OutboundIntegrationConfig_dispatchMode';");

            // ── 2. Widen into the unified plane. ─────────────────────────────────────────────────────────
            migrationBuilder.AlterColumn<string>(
                name: "transactionType", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(60)", maxLength: 60, nullable: true,
                oldClrType: typeof(string), oldType: "nvarchar(60)", oldMaxLength: 60);
            migrationBuilder.AlterColumn<string>(
                name: "responseMappingExpr", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(max)", nullable: true,
                oldClrType: typeof(string), oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<string>(name: "kind", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Transaction");
            migrationBuilder.AddColumn<Guid>(name: "connectionPointId", schema: "integration", table: "OutboundIntegrationConfig",
                type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<string>(name: "attachmentType", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(50)", maxLength: 50, nullable: true);
            migrationBuilder.AddColumn<string>(name: "targetEntityName", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(100)", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<string>(name: "contextJson", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(max)", nullable: true);
            migrationBuilder.AddColumn<string>(name: "mutatePath", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(400)", maxLength: 400, nullable: true);
            migrationBuilder.AddColumn<string>(name: "mutateVerb", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(10)", maxLength: 10, nullable: true);
            migrationBuilder.AddColumn<string>(name: "deletePath", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(400)", maxLength: 400, nullable: true);
            migrationBuilder.AddColumn<string>(name: "deleteVerb", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(10)", maxLength: 10, nullable: true);
            migrationBuilder.AddColumn<string>(name: "staticHeadersJson", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(max)", nullable: true);
            migrationBuilder.AddColumn<string>(name: "requestFormat", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "Json");
            migrationBuilder.AddColumn<string>(name: "responseFormat", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "Json");
            migrationBuilder.AddColumn<string>(name: "mutateMappingExpr", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(max)", nullable: true);
            migrationBuilder.AddColumn<string>(name: "mutateMappingSeedHash", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(64)", maxLength: 64, nullable: true);

            migrationBuilder.AddCheckConstraint(name: "CK_OutboundIntegrationConfig_kind",
                schema: "integration", table: "OutboundIntegrationConfig", sql: "[kind] IN ('Transaction','Document')");
            migrationBuilder.AddCheckConstraint(name: "CK_OutboundIntegrationConfig_mutateVerb",
                schema: "integration", table: "OutboundIntegrationConfig", sql: "[mutateVerb] IS NULL OR [mutateVerb] IN ('POST','PUT','PATCH')");
            migrationBuilder.AddCheckConstraint(name: "CK_OutboundIntegrationConfig_deleteVerb",
                schema: "integration", table: "OutboundIntegrationConfig", sql: "[deleteVerb] IS NULL OR [deleteVerb] IN ('POST','PUT','PATCH','DELETE')");
            migrationBuilder.AddCheckConstraint(name: "CK_OutboundIntegrationConfig_requestFormat",
                schema: "integration", table: "OutboundIntegrationConfig", sql: "[requestFormat] IN ('Json','Xml')");
            migrationBuilder.AddCheckConstraint(name: "CK_OutboundIntegrationConfig_responseFormat",
                schema: "integration", table: "OutboundIntegrationConfig", sql: "[responseFormat] IN ('Json','Xml')");

            // ── 3. ConnectionPoint. ──────────────────────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "ConnectionPoint",
                schema: "integration",
                columns: table => new
                {
                    connectionPointId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    systemType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    baseUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    authConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    isDefault = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    connectionPointSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_ConnectionPoint", x => x.connectionPointId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_ConnectionPoint_systemType", "[systemType] IN ('InforION','Tally','ClearTax','GenericRest')");
                });

            migrationBuilder.AddForeignKey(
                name: "FK_OutboundIntegrationConfig_ConnectionPoint_ConnectionPointId",
                schema: "integration",
                table: "OutboundIntegrationConfig",
                column: "connectionPointId",
                principalSchema: "integration",
                principalTable: "ConnectionPoint",
                principalColumn: "connectionPointId",
                onDelete: ReferentialAction.Restrict);

            // ── 4. Convert the R8 IDM config pair into Document-kind rows (GUID identity of the mapping row
            //       is preserved; transport verb/path/headers + acl/entityName context join from the tenant's
            //       IDM.Item.* rows; response stays on the code parser — responseMappingExpr NULL — with the
            //       body declared Xml so a future expression runs over normalized JSON). ─────────────────────
            migrationBuilder.Sql(@"
INSERT INTO [integration].[OutboundIntegrationConfig]
    (outboundIntegrationConfigId, tenantId, kind, connectionPointId, transactionType, portalEntity,
     attachmentType, targetEntityName, contextJson, endpointPath, httpVerb,
     mutatePath, mutateVerb, deletePath, deleteVerb, staticHeadersJson,
     requestFormat, responseFormat, dispatchMode, eligibilityGateExpr,
     requestMappingExpr, mutateMappingExpr, responseMappingExpr, ackMappingExpr,
     requestMappingSeedHash, mutateMappingSeedHash, gateVersion, pathConfirmed,
     createdOn, createdBy, updatedOn, updatedBy)
SELECT
    a.idmAttachmentTypeConfigId,
    a.tenantId,
    'Document',
    NULL,
    NULL,
    COALESCE(a.ownerEntityType,
        CASE a.idmEntityType
            WHEN 'InforAdvanceShipmentNoticeSupplierASN' THEN 'Asn'
            WHEN 'InforInvoice' THEN 'Invoice'
            ELSE 'Supplier' END),
    a.attachmentType,
    a.idmEntityType,
    CASE WHEN ec.defaultAcl IS NOT NULL OR ec.entityName IS NOT NULL
         THEN CONCAT('{""acl"":""', ISNULL(ec.defaultAcl, 'Public'), '"",""entityName"":""', ISNULL(ec.entityName, 'MDS_GenericDocument'), '""}')
         ELSE NULL END,
    ISNULL(ec.relativePath, '/IDM/api/items'),
    CASE WHEN ec.httpMethod IN ('POST','PUT','PATCH') THEN ec.httpMethod ELSE 'POST' END,
    eu.relativePath,
    CASE WHEN eu.httpMethod IN ('POST','PUT','PATCH') THEN eu.httpMethod ELSE NULL END,
    ed.relativePath,
    CASE WHEN ed.httpMethod IN ('POST','PUT','PATCH','DELETE') THEN ed.httpMethod ELSE 'DELETE' END,
    ec.staticHeadersJson,
    'Json', 'Xml',
    CASE WHEN a.isEnabled = 1 THEN 'Dynamic' ELSE 'Held' END,
    a.eligibilityGateExpr,
    a.createMappingExpression,
    a.mutateMappingExpression,
    NULL, NULL,
    a.createMappingSeedHash,
    a.mutateMappingSeedHash,
    1, 0,
    a.createdOn, a.createdBy, a.updatedOn, a.updatedBy
FROM (
    -- Dedupe: a pre-2026-07-06 NULL-owner row and its stored-owner successor resolve to the SAME
    -- (tenant, portalEntity, attachmentType) identity after COALESCE — the old UQ treated NULL as a
    -- distinct value, the new per-kind UQ does not. The stored-owner row wins; ties → newest (Seq).
    SELECT *,
           ROW_NUMBER() OVER (
               PARTITION BY x.tenantId,
                            COALESCE(x.ownerEntityType,
                                CASE x.idmEntityType
                                    WHEN 'InforAdvanceShipmentNoticeSupplierASN' THEN 'Asn'
                                    WHEN 'InforInvoice' THEN 'Invoice'
                                    ELSE 'Supplier' END),
                            x.attachmentType
               ORDER BY CASE WHEN x.ownerEntityType IS NOT NULL THEN 0 ELSE 1 END,
                        x.idmAttachmentTypeConfigSeq DESC) AS rn
    FROM [integration].[IdmAttachmentTypeConfig] x
    WHERE x.isDeleted = 0
) a
OUTER APPLY (SELECT TOP 1 e.defaultAcl, e.entityName, e.relativePath, e.httpMethod, e.staticHeadersJson
             FROM [integration].[OutboundEndpointConfig] e
             WHERE (e.tenantId = a.tenantId OR (e.tenantId IS NULL AND a.tenantId IS NULL))
               AND e.endpointKey = 'IDM.Item.Create' AND e.isDeleted = 0) ec
OUTER APPLY (SELECT TOP 1 e.relativePath, e.httpMethod
             FROM [integration].[OutboundEndpointConfig] e
             WHERE (e.tenantId = a.tenantId OR (e.tenantId IS NULL AND a.tenantId IS NULL))
               AND e.endpointKey = 'IDM.Item.Update' AND e.isDeleted = 0) eu
OUTER APPLY (SELECT TOP 1 e.relativePath, e.httpMethod
             FROM [integration].[OutboundEndpointConfig] e
             WHERE (e.tenantId = a.tenantId OR (e.tenantId IS NULL AND a.tenantId IS NULL))
               AND e.endpointKey = 'IDM.Item.Delete' AND e.isDeleted = 0) ed
WHERE a.rn = 1;
");

            // ── 5. The R8 pair is now redundant. ─────────────────────────────────────────────────────────
            migrationBuilder.DropTable(name: "IdmAttachmentTypeConfig", schema: "integration");
            migrationBuilder.DropTable(name: "OutboundEndpointConfig", schema: "integration");

            migrationBuilder.CreateIndex(
                name: "UQ_ConnectionPoint_tenant_default",
                schema: "integration",
                table: "ConnectionPoint",
                column: "tenantId",
                unique: true,
                filter: "[isDefault] = 1 AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UQ_ConnectionPoint_tenant_name",
                schema: "integration",
                table: "ConnectionPoint",
                columns: new[] { "tenantId", "name" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_ConnectionPoint_connectionPointSeq",
                schema: "integration",
                table: "ConnectionPoint",
                column: "connectionPointSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboundIntegrationConfig_connectionPointId",
                schema: "integration",
                table: "OutboundIntegrationConfig",
                column: "connectionPointId");

            migrationBuilder.CreateIndex(
                name: "UQ_OutboundIntegrationConfig_tenant_entity_attachmentType",
                schema: "integration",
                table: "OutboundIntegrationConfig",
                columns: new[] { "tenantId", "portalEntity", "attachmentType" },
                unique: true,
                filter: "[isDeleted] = 0 AND [kind] = 'Document'");

            migrationBuilder.CreateIndex(
                name: "UQ_OutboundIntegrationConfig_tenant_transactionType",
                schema: "integration",
                table: "OutboundIntegrationConfig",
                columns: new[] { "tenantId", "transactionType" },
                unique: true,
                filter: "[isDeleted] = 0 AND [kind] = 'Transaction'");

            // (UX_OutboundIntegrationConfig_outboundIntegrationConfigSeq already exists — renamed in step 1.)

            migrationBuilder.AddForeignKey(
                name: "FK_LnBackfillRun_OutboundIntegrationConfig_OutboundIntegrationConfigId",
                schema: "integration",
                table: "LnBackfillRun",
                column: "lnEndpointConfigId",
                principalSchema: "integration",
                principalTable: "OutboundIntegrationConfig",
                principalColumn: "outboundIntegrationConfigId",
                onDelete: ReferentialAction.Restrict);
        }

        // HAND-SHAPED reverse: recreate the R8 pair, reverse-copy Document rows (transport rows are
        // reconstructed BEST-EFFORT from the unified routing columns — per-tenant IDM.Item.* rows), remove the
        // Document rows + the R10 columns, and rename the table back. Data added ONLY via R10 features
        // (connection tags, formats, mutate/delete routing) is lost by design on rollback.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LnBackfillRun_OutboundIntegrationConfig_OutboundIntegrationConfigId",
                schema: "integration",
                table: "LnBackfillRun");

            migrationBuilder.CreateTable(
                name: "IdmAttachmentTypeConfig",
                schema: "integration",
                columns: table => new
                {
                    idmAttachmentTypeConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    attachmentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    createMappingExpression = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    createMappingSeedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    eligibilityGateExpr = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    idmEntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    isEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    mutateMappingExpression = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    mutateMappingSeedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ownerEntityType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    idmAttachmentTypeConfigSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdmAttachmentTypeConfig", x => x.idmAttachmentTypeConfigId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "OutboundEndpointConfig",
                schema: "integration",
                columns: table => new
                {
                    outboundEndpointConfigId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    ackParserKey = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    createdBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    createdOn = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    defaultAcl = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    deletedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    deletedOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    endpointKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    entityName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    httpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    isDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    isEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    relativePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    outboundEndpointConfigSeq = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    staticHeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    targetSystem = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    updatedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    updatedOn = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundEndpointConfig", x => x.outboundEndpointConfigId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_OutboundEndpointConfig_httpMethod", "[httpMethod] IN ('POST','PUT','DELETE')");
                });

            migrationBuilder.CreateIndex(
                name: "UQ_IdmAttachmentTypeConfig_tenant_owner_attachmentType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                columns: new[] { "tenantId", "ownerEntityType", "attachmentType" },
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

            // Reverse-copy Document rows → the R8 pair. Transport rows are reconstructed best-effort
            // (3 IDM.Item.* rows per tenant that has any Document row, from the unified routing columns).
            migrationBuilder.Sql(@"
INSERT INTO [integration].[IdmAttachmentTypeConfig]
    (idmAttachmentTypeConfigId, tenantId, ownerEntityType, attachmentType, idmEntityType,
     eligibilityGateExpr, createMappingExpression, mutateMappingExpression,
     createMappingSeedHash, mutateMappingSeedHash, isEnabled,
     createdOn, createdBy, updatedOn, updatedBy)
SELECT c.outboundIntegrationConfigId, c.tenantId, c.portalEntity, c.attachmentType, ISNULL(c.targetEntityName, ''),
       ISNULL(c.eligibilityGateExpr, ''), c.requestMappingExpr, c.mutateMappingExpr,
       c.requestMappingSeedHash, c.mutateMappingSeedHash,
       CASE WHEN c.dispatchMode = 'Dynamic' THEN 1 ELSE 0 END,
       c.createdOn, c.createdBy, c.updatedOn, c.updatedBy
FROM [integration].[OutboundIntegrationConfig] c
WHERE c.kind = 'Document' AND c.isDeleted = 0;

INSERT INTO [integration].[OutboundEndpointConfig]
    (outboundEndpointConfigId, tenantId, targetSystem, endpointKey, httpMethod, relativePath,
     staticHeadersJson, ackParserKey, defaultAcl, entityName, isEnabled, createdOn, createdBy)
SELECT NEWID(), t.tenantId, 'IDM', k.endpointKey,
       CASE k.endpointKey WHEN 'IDM.Item.Delete' THEN ISNULL(t.deleteVerb, 'DELETE')
                          WHEN 'IDM.Item.Update' THEN ISNULL(t.mutateVerb, t.httpVerb)
                          ELSE t.httpVerb END,
       CASE k.endpointKey WHEN 'IDM.Item.Delete' THEN ISNULL(t.deletePath, t.endpointPath)
                          WHEN 'IDM.Item.Update' THEN ISNULL(t.mutatePath, t.endpointPath)
                          ELSE t.endpointPath END,
       CASE WHEN k.endpointKey = 'IDM.Item.Create' THEN t.staticHeadersJson ELSE NULL END,
       'IdmXml', 'Public', 'MDS_GenericDocument', 0, SYSUTCDATETIME(), 'migration:0051-down'
FROM (SELECT c.tenantId, MIN(c.endpointPath) AS endpointPath, MIN(c.httpVerb) AS httpVerb,
             MIN(c.mutatePath) AS mutatePath, MIN(c.mutateVerb) AS mutateVerb,
             MIN(c.deletePath) AS deletePath, MIN(c.deleteVerb) AS deleteVerb,
             MIN(c.staticHeadersJson) AS staticHeadersJson
      FROM [integration].[OutboundIntegrationConfig] c
      WHERE c.kind = 'Document' AND c.isDeleted = 0
      GROUP BY c.tenantId) t
CROSS JOIN (VALUES ('IDM.Item.Create'), ('IDM.Item.Update'), ('IDM.Item.Delete')) k(endpointKey);

DELETE FROM [integration].[OutboundIntegrationConfig] WHERE [kind] = 'Document';
");

            // Un-widen + rename back.
            migrationBuilder.DropForeignKey(
                name: "FK_OutboundIntegrationConfig_ConnectionPoint_ConnectionPointId",
                schema: "integration", table: "OutboundIntegrationConfig");
            migrationBuilder.DropIndex(
                name: "IX_OutboundIntegrationConfig_connectionPointId",
                schema: "integration", table: "OutboundIntegrationConfig");
            migrationBuilder.DropTable(name: "ConnectionPoint", schema: "integration");

            migrationBuilder.DropIndex(name: "UQ_OutboundIntegrationConfig_tenant_transactionType",
                schema: "integration", table: "OutboundIntegrationConfig");
            migrationBuilder.DropIndex(name: "UQ_OutboundIntegrationConfig_tenant_entity_attachmentType",
                schema: "integration", table: "OutboundIntegrationConfig");

            migrationBuilder.DropCheckConstraint(name: "CK_OutboundIntegrationConfig_kind",
                schema: "integration", table: "OutboundIntegrationConfig");
            migrationBuilder.DropCheckConstraint(name: "CK_OutboundIntegrationConfig_mutateVerb",
                schema: "integration", table: "OutboundIntegrationConfig");
            migrationBuilder.DropCheckConstraint(name: "CK_OutboundIntegrationConfig_deleteVerb",
                schema: "integration", table: "OutboundIntegrationConfig");
            migrationBuilder.DropCheckConstraint(name: "CK_OutboundIntegrationConfig_requestFormat",
                schema: "integration", table: "OutboundIntegrationConfig");
            migrationBuilder.DropCheckConstraint(name: "CK_OutboundIntegrationConfig_responseFormat",
                schema: "integration", table: "OutboundIntegrationConfig");

            foreach (var col in new[]
            {
                "kind", "connectionPointId", "attachmentType", "targetEntityName", "contextJson",
                "mutatePath", "mutateVerb", "deletePath", "deleteVerb", "staticHeadersJson",
                "requestFormat", "responseFormat", "mutateMappingExpr", "mutateMappingSeedHash",
            })
            {
                migrationBuilder.DropColumn(name: col, schema: "integration", table: "OutboundIntegrationConfig");
            }

            migrationBuilder.AlterColumn<string>(
                name: "transactionType", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(60)", maxLength: 60, nullable: false, defaultValue: "",
                oldClrType: typeof(string), oldType: "nvarchar(60)", oldMaxLength: 60, oldNullable: true);
            migrationBuilder.AlterColumn<string>(
                name: "responseMappingExpr", schema: "integration", table: "OutboundIntegrationConfig",
                type: "nvarchar(max)", nullable: false, defaultValue: "",
                oldClrType: typeof(string), oldType: "nvarchar(max)", oldNullable: true);

            migrationBuilder.Sql("EXEC sp_rename N'[integration].[CK_OutboundIntegrationConfig_httpVerb]', N'CK_LnEndpointConfig_httpVerb';");
            migrationBuilder.Sql("EXEC sp_rename N'[integration].[CK_OutboundIntegrationConfig_dispatchMode]', N'CK_LnEndpointConfig_dispatchMode';");
            migrationBuilder.Sql("EXEC sp_rename N'[integration].[PK_OutboundIntegrationConfig]', N'PK_LnEndpointConfig';");
            migrationBuilder.RenameIndex(
                name: "UX_OutboundIntegrationConfig_outboundIntegrationConfigSeq", schema: "integration",
                table: "OutboundIntegrationConfig", newName: "UX_LnEndpointConfig_lnEndpointConfigSeq");
            migrationBuilder.RenameColumn(
                name: "outboundIntegrationConfigId", schema: "integration", table: "OutboundIntegrationConfig",
                newName: "lnEndpointConfigId");
            migrationBuilder.RenameColumn(
                name: "outboundIntegrationConfigSeq", schema: "integration", table: "OutboundIntegrationConfig",
                newName: "lnEndpointConfigSeq");
            migrationBuilder.RenameTable(
                name: "OutboundIntegrationConfig", schema: "integration",
                newName: "LnEndpointConfig", newSchema: "integration");

            migrationBuilder.CreateIndex(
                name: "UQ_LnEndpointConfig_tenant_transactionType",
                schema: "integration",
                table: "LnEndpointConfig",
                columns: new[] { "tenantId", "transactionType" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_LnBackfillRun_LnEndpointConfig_LnEndpointConfigId",
                schema: "integration",
                table: "LnBackfillRun",
                column: "lnEndpointConfigId",
                principalSchema: "integration",
                principalTable: "LnEndpointConfig",
                principalColumn: "lnEndpointConfigId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
