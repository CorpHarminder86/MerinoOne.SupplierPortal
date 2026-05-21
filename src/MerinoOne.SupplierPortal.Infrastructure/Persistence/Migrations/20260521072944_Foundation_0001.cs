using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Foundation_0001 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "admin");

            migrationBuilder.EnsureSchema(
                name: "proc");

            migrationBuilder.EnsureSchema(
                name: "comm");

            migrationBuilder.EnsureSchema(
                name: "doc");

            migrationBuilder.EnsureSchema(
                name: "integration");

            migrationBuilder.EnsureSchema(
                name: "supplier");

            migrationBuilder.CreateTable(
                name: "AppUser",
                schema: "admin",
                columns: table => new
                {
                    appUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    userCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    fullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    passwordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    isInternal = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    isMfaEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    appUserSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AppUser", x => x.appUserId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "InforEndpointMap",
                schema: "integration",
                columns: table => new
                {
                    inforEndpointMapId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    entityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    inforEndpointUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    bodName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    isEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    inforEndpointMapSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_InforEndpointMap", x => x.inforEndpointMapId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "InforSyncLog",
                schema: "integration",
                columns: table => new
                {
                    inforSyncLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    entityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    direction = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    payloadRef = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    idempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    syncedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    errorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    inforSyncLogSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_InforSyncLog", x => x.inforSyncLogId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Permission",
                schema: "admin",
                columns: table => new
                {
                    permissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    code = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    module = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    permissionSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Permission", x => x.permissionId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Role",
                schema: "admin",
                columns: table => new
                {
                    roleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    roleSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Role", x => x.roleId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Tenant",
                schema: "admin",
                columns: table => new
                {
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    tenantSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Tenant", x => x.tenantId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Seccode",
                schema: "admin",
                columns: table => new
                {
                    seccodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    seccodeType = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    appUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    seccodeSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Seccode", x => x.seccodeId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Seccode_seccodeType", "[seccodeType] IN ('U','G')");
                    table.ForeignKey(
                        name: "FK_Seccode_AppUser_AppUserId",
                        column: x => x.appUserId,
                        principalSchema: "admin",
                        principalTable: "AppUser",
                        principalColumn: "appUserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationError",
                schema: "integration",
                columns: table => new
                {
                    integrationErrorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    syncLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    entityName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    errorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    stackTrace = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    retryCount = table.Column<int>(type: "int", nullable: false),
                    lastRetriedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    isResolved = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    resolutionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    integrationErrorSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_IntegrationError", x => x.integrationErrorId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_IntegrationError_SyncLog_SyncLogId",
                        column: x => x.syncLogId,
                        principalSchema: "integration",
                        principalTable: "InforSyncLog",
                        principalColumn: "inforSyncLogId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RolePermission",
                schema: "admin",
                columns: table => new
                {
                    rolePermissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    roleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    permissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    rolePermissionSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_RolePermission", x => x.rolePermissionId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_RolePermission_Permission_PermissionId",
                        column: x => x.permissionId,
                        principalSchema: "admin",
                        principalTable: "Permission",
                        principalColumn: "permissionId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RolePermission_Role_RoleId",
                        column: x => x.roleId,
                        principalSchema: "admin",
                        principalTable: "Role",
                        principalColumn: "roleId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRole",
                schema: "admin",
                columns: table => new
                {
                    userRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    appUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    roleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    userRoleSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_UserRole", x => x.userRoleId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_UserRole_AppUser_AppUserId",
                        column: x => x.appUserId,
                        principalSchema: "admin",
                        principalTable: "AppUser",
                        principalColumn: "appUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRole_Role_RoleId",
                        column: x => x.roleId,
                        principalSchema: "admin",
                        principalTable: "Role",
                        principalColumn: "roleId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommunicationMessage",
                schema: "comm",
                columns: table => new
                {
                    communicationMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    purchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    threadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    senderUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    receiverUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    messageBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    attachmentUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    sentAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    isRead = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    readAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    isSystemMessage = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    communicationMessageSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_CommunicationMessage", x => x.communicationMessageId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_CommunicationMessage_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentUpload",
                schema: "doc",
                columns: table => new
                {
                    documentUploadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    ownerEntityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ownerEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    documentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    fileName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    fileUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    fileSizeKb = table.Column<long>(type: "bigint", nullable: false),
                    mimeType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    uploadedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    aiValidationStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    aiValidationConfidence = table.Column<decimal>(type: "decimal(5,2)", nullable: true),
                    aiValidationPayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    aiValidatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    documentUploadSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_DocumentUpload", x => x.documentUploadId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_DocumentUpload_aiValidationStatus", "[aiValidationStatus] IN ('Pending','Valid','Flagged','Skipped')");
                    table.ForeignKey(
                        name: "FK_DocumentUpload_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrder",
                schema: "proc",
                columns: table => new
                {
                    purchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    poNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    buyerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    poType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    poDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    paymentTerms = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    deliveryTerms = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    poStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    acknowledgmentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    acceptedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    rejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    proposedDeliveryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    version = table.Column<int>(type: "int", nullable: false),
                    erpSyncId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    purchaseOrderSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_PurchaseOrder", x => x.purchaseOrderId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_PurchaseOrder_poType", "[poType] IN ('Material','Service')");
                    table.ForeignKey(
                        name: "FK_PurchaseOrder_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SecRight",
                schema: "admin",
                columns: table => new
                {
                    secRightId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    seccodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    userCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    canRead = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    canWrite = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    secRightSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SecRight", x => x.secRightId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SecRight_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Supplier",
                schema: "supplier",
                columns: table => new
                {
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    legalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    tradeName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    supplierType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    gstNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    panNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    msmeRegNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    msmeCategory = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    gstValidated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    panValidated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    msmeValidated = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    registrationStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    invitedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    invitedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    approvedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    approvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    approvalOverrideComment = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    rejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    website = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    isActiveSupplier = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    supplierSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Supplier", x => x.supplierId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Supplier_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Asn",
                schema: "proc",
                columns: table => new
                {
                    asnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    asnNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    purchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    expectedDeliveryDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    timeWindow = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    carrierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    trackingNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    vehicleNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    driverName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    driverPhone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    asnStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    asnSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Asn", x => x.asnId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Asn_PurchaseOrder_PurchaseOrderId",
                        column: x => x.purchaseOrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "purchaseOrderId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Asn_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeliverySchedule",
                schema: "proc",
                columns: table => new
                {
                    deliveryScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    purchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    proposedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    timeWindow = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    vehicleInfo = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    scheduleStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    approvedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    rejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    deliveryScheduleSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_DeliverySchedule", x => x.deliveryScheduleId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_DeliverySchedule_PurchaseOrder_PurchaseOrderId",
                        column: x => x.purchaseOrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "purchaseOrderId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeliverySchedule_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLine",
                schema: "proc",
                columns: table => new
                {
                    purchaseOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    purchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    positionNo = table.Column<int>(type: "int", nullable: false),
                    sequenceNo = table.Column<int>(type: "int", nullable: false),
                    itemCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    itemDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    orderUnit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    orderQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    priceUnit = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    price = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    discountPct = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    discountAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    deliveryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    taxCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    taxDescription = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    purchaseOrderLineSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_PurchaseOrderLine", x => x.purchaseOrderLineId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLine_PurchaseOrder_PurchaseOrderId",
                        column: x => x.purchaseOrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "purchaseOrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierUserMap",
                schema: "admin",
                columns: table => new
                {
                    supplierUserMapId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    appUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    secRightId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplierUserMapSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SupplierUserMap", x => x.supplierUserMapId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SupplierUserMap_AppUser_AppUserId",
                        column: x => x.appUserId,
                        principalSchema: "admin",
                        principalTable: "AppUser",
                        principalColumn: "appUserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierUserMap_SecRight_SecRightId",
                        column: x => x.secRightId,
                        principalSchema: "admin",
                        principalTable: "SecRight",
                        principalColumn: "secRightId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierAddress",
                schema: "supplier",
                columns: table => new
                {
                    supplierAddressId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    addressType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    addressLine1 = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    addressLine2 = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    city = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    pincode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    supplierAddressSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SupplierAddress", x => x.supplierAddressId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SupplierAddress_Supplier_SupplierId",
                        column: x => x.supplierId,
                        principalSchema: "supplier",
                        principalTable: "Supplier",
                        principalColumn: "supplierId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierContact",
                schema: "supplier",
                columns: table => new
                {
                    supplierContactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    contactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    designation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    isPrimary = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    supplierContactSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SupplierContact", x => x.supplierContactId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SupplierContact_Supplier_SupplierId",
                        column: x => x.supplierId,
                        principalSchema: "supplier",
                        principalTable: "Supplier",
                        principalColumn: "supplierId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierVerification",
                schema: "supplier",
                columns: table => new
                {
                    supplierVerificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    verificationType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    attemptedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    attemptedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    providerName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    result = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    responsePayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    comments = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    supplierVerificationSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SupplierVerification", x => x.supplierVerificationId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_SupplierVerification_result", "[result] IN ('Pass','Fail','Error')");
                    table.CheckConstraint("CK_SupplierVerification_verificationType", "[verificationType] IN ('GST','PAN','MSME')");
                    table.ForeignKey(
                        name: "FK_SupplierVerification_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierVerification_Supplier_SupplierId",
                        column: x => x.supplierId,
                        principalSchema: "supplier",
                        principalTable: "Supplier",
                        principalColumn: "supplierId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invoice",
                schema: "proc",
                columns: table => new
                {
                    invoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    invoiceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    purchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    asnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    invoiceDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    invoiceAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    taxAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    netAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    currencyCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    matchingType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    grnReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    invoiceStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    rejectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    eInvoiceIrn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    eInvoiceAckNo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    eWayBillNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    submittedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    approvedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    approvedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    invoiceSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Invoice", x => x.invoiceId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Invoice_Asn_AsnId",
                        column: x => x.asnId,
                        principalSchema: "proc",
                        principalTable: "Asn",
                        principalColumn: "asnId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_PurchaseOrder_PurchaseOrderId",
                        column: x => x.purchaseOrderId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrder",
                        principalColumn: "purchaseOrderId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoice_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AsnLine",
                schema: "proc",
                columns: table => new
                {
                    asnLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    asnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    purchaseOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    shippedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    batchNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    expiryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    asnLineSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_AsnLine", x => x.asnLineId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AsnLine_Asn_AsnId",
                        column: x => x.asnId,
                        principalSchema: "proc",
                        principalTable: "Asn",
                        principalColumn: "asnId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AsnLine_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.purchaseOrderLineId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "purchaseOrderLineId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GoodsReceipt",
                schema: "proc",
                columns: table => new
                {
                    goodsReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    grnNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    purchaseOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    asnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    receivedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    shortQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    rejectedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    grnDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    erpSyncId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    goodsReceiptSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_GoodsReceipt", x => x.goodsReceiptId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_GoodsReceipt_Asn_AsnId",
                        column: x => x.asnId,
                        principalSchema: "proc",
                        principalTable: "Asn",
                        principalColumn: "asnId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GoodsReceipt_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.purchaseOrderLineId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "purchaseOrderLineId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GoodsReceipt_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CreditDebitNote",
                schema: "proc",
                columns: table => new
                {
                    creditDebitNoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    noteNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    noteType = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    invoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    amount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    noteStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    creditDebitNoteSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_CreditDebitNote", x => x.creditDebitNoteId)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_CreditDebitNote_noteType", "[noteType] IN ('CN','DN')");
                    table.ForeignKey(
                        name: "FK_CreditDebitNote_Invoice_InvoiceId",
                        column: x => x.invoiceId,
                        principalSchema: "proc",
                        principalTable: "Invoice",
                        principalColumn: "invoiceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditDebitNote_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLine",
                schema: "proc",
                columns: table => new
                {
                    invoiceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    invoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    purchaseOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    itemCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    itemDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    billedQty = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    unitPrice = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    lineAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    taxCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    taxAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    invoiceLineSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_InvoiceLine", x => x.invoiceLineId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_Invoice_InvoiceId",
                        column: x => x.invoiceId,
                        principalSchema: "proc",
                        principalTable: "Invoice",
                        principalColumn: "invoiceId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_PurchaseOrderLine_PurchaseOrderLineId",
                        column: x => x.purchaseOrderLineId,
                        principalSchema: "proc",
                        principalTable: "PurchaseOrderLine",
                        principalColumn: "purchaseOrderLineId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Payment",
                schema: "proc",
                columns: table => new
                {
                    paymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    paymentReference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    invoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    paymentDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    paymentAmount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    paymentMode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    bankName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    bankAccountRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    tdsDeducted = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    tdsSection = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    netPaid = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    remarks = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    remittancePdfUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    erpSyncId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    paymentSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Payment", x => x.paymentId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Payment_Invoice_InvoiceId",
                        column: x => x.invoiceId,
                        principalSchema: "proc",
                        principalTable: "Invoice",
                        principalColumn: "invoiceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payment_Seccode_SeccodeId",
                        column: x => x.seccodeId,
                        principalSchema: "admin",
                        principalTable: "Seccode",
                        principalColumn: "seccodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_AppUser_email",
                schema: "admin",
                table: "AppUser",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_AppUser_userCode",
                schema: "admin",
                table: "AppUser",
                column: "userCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AppUser_appUserSeq",
                schema: "admin",
                table: "AppUser",
                column: "appUserSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Asn_purchaseOrderId",
                schema: "proc",
                table: "Asn",
                column: "purchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Asn_seccodeId",
                schema: "proc",
                table: "Asn",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Asn_supplierId",
                schema: "proc",
                table: "Asn",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "UQ_Asn_asnNumber",
                schema: "proc",
                table: "Asn",
                column: "asnNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Asn_asnSeq",
                schema: "proc",
                table: "Asn",
                column: "asnSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AsnLine_asnId",
                schema: "proc",
                table: "AsnLine",
                column: "asnId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnLine_purchaseOrderLineId",
                schema: "proc",
                table: "AsnLine",
                column: "purchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "UX_AsnLine_asnLineSeq",
                schema: "proc",
                table: "AsnLine",
                column: "asnLineSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationMessage_seccodeId",
                schema: "comm",
                table: "CommunicationMessage",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationMessage_senderUserId",
                schema: "comm",
                table: "CommunicationMessage",
                column: "senderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunicationMessage_threadId",
                schema: "comm",
                table: "CommunicationMessage",
                column: "threadId");

            migrationBuilder.CreateIndex(
                name: "UX_CommunicationMessage_communicationMessageSeq",
                schema: "comm",
                table: "CommunicationMessage",
                column: "communicationMessageSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditDebitNote_invoiceId",
                schema: "proc",
                table: "CreditDebitNote",
                column: "invoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditDebitNote_seccodeId",
                schema: "proc",
                table: "CreditDebitNote",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "UX_CreditDebitNote_creditDebitNoteSeq",
                schema: "proc",
                table: "CreditDebitNote",
                column: "creditDebitNoteSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliverySchedule_purchaseOrderId",
                schema: "proc",
                table: "DeliverySchedule",
                column: "purchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliverySchedule_seccodeId",
                schema: "proc",
                table: "DeliverySchedule",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "UX_DeliverySchedule_deliveryScheduleSeq",
                schema: "proc",
                table: "DeliverySchedule",
                column: "deliveryScheduleSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentUpload_owner",
                schema: "doc",
                table: "DocumentUpload",
                columns: new[] { "ownerEntityType", "ownerEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentUpload_seccodeId",
                schema: "doc",
                table: "DocumentUpload",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "UX_DocumentUpload_documentUploadSeq",
                schema: "doc",
                table: "DocumentUpload",
                column: "documentUploadSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_asnId",
                schema: "proc",
                table: "GoodsReceipt",
                column: "asnId");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_grnNumber",
                schema: "proc",
                table: "GoodsReceipt",
                column: "grnNumber");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_purchaseOrderLineId",
                schema: "proc",
                table: "GoodsReceipt",
                column: "purchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_seccodeId",
                schema: "proc",
                table: "GoodsReceipt",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "UX_GoodsReceipt_goodsReceiptSeq",
                schema: "proc",
                table: "GoodsReceipt",
                column: "goodsReceiptSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_InforEndpointMap_inforEndpointMapSeq",
                schema: "integration",
                table: "InforEndpointMap",
                column: "inforEndpointMapSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_InforSyncLog_idempotencyKey",
                schema: "integration",
                table: "InforSyncLog",
                column: "idempotencyKey");

            migrationBuilder.CreateIndex(
                name: "IX_InforSyncLog_syncedAt",
                schema: "integration",
                table: "InforSyncLog",
                column: "syncedAt");

            migrationBuilder.CreateIndex(
                name: "UX_InforSyncLog_inforSyncLogSeq",
                schema: "integration",
                table: "InforSyncLog",
                column: "inforSyncLogSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationError_syncLogId",
                schema: "integration",
                table: "IntegrationError",
                column: "syncLogId");

            migrationBuilder.CreateIndex(
                name: "UX_IntegrationError_integrationErrorSeq",
                schema: "integration",
                table: "IntegrationError",
                column: "integrationErrorSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_asnId",
                schema: "proc",
                table: "Invoice",
                column: "asnId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_invoiceStatus",
                schema: "proc",
                table: "Invoice",
                column: "invoiceStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_purchaseOrderId",
                schema: "proc",
                table: "Invoice",
                column: "purchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_seccodeId",
                schema: "proc",
                table: "Invoice",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "UQ_Invoice_supplier_invoiceNumber",
                schema: "proc",
                table: "Invoice",
                columns: new[] { "supplierId", "invoiceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Invoice_invoiceSeq",
                schema: "proc",
                table: "Invoice",
                column: "invoiceSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_invoiceId",
                schema: "proc",
                table: "InvoiceLine",
                column: "invoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_purchaseOrderLineId",
                schema: "proc",
                table: "InvoiceLine",
                column: "purchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "UX_InvoiceLine_invoiceLineSeq",
                schema: "proc",
                table: "InvoiceLine",
                column: "invoiceLineSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Payment_invoiceId",
                schema: "proc",
                table: "Payment",
                column: "invoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_seccodeId",
                schema: "proc",
                table: "Payment",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_supplierId",
                schema: "proc",
                table: "Payment",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "UX_Payment_paymentSeq",
                schema: "proc",
                table: "Payment",
                column: "paymentSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_Permission_code",
                schema: "admin",
                table: "Permission",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Permission_permissionSeq",
                schema: "admin",
                table: "Permission",
                column: "permissionSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_poStatus",
                schema: "proc",
                table: "PurchaseOrder",
                column: "poStatus");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_seccodeId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_supplierId",
                schema: "proc",
                table: "PurchaseOrder",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "UQ_PurchaseOrder_poNumber",
                schema: "proc",
                table: "PurchaseOrder",
                column: "poNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrder_purchaseOrderSeq",
                schema: "proc",
                table: "PurchaseOrder",
                column: "purchaseOrderSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_purchaseOrderId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "purchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrderLine_purchaseOrderLineSeq",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "purchaseOrderLineSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_Role_name",
                schema: "admin",
                table: "Role",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Role_roleSeq",
                schema: "admin",
                table: "Role",
                column: "roleSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_RolePermission_permissionId",
                schema: "admin",
                table: "RolePermission",
                column: "permissionId");

            migrationBuilder.CreateIndex(
                name: "UQ_RolePermission_role_permission",
                schema: "admin",
                table: "RolePermission",
                columns: new[] { "roleId", "permissionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RolePermission_rolePermissionSeq",
                schema: "admin",
                table: "RolePermission",
                column: "rolePermissionSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Seccode_appUserId",
                schema: "admin",
                table: "Seccode",
                column: "appUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Seccode_supplierId",
                schema: "admin",
                table: "Seccode",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "UX_Seccode_seccodeSeq",
                schema: "admin",
                table: "Seccode",
                column: "seccodeSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SecRight_userCode",
                schema: "admin",
                table: "SecRight",
                column: "userCode");

            migrationBuilder.CreateIndex(
                name: "UX_SecRight_seccodeId_userCode",
                schema: "admin",
                table: "SecRight",
                columns: new[] { "seccodeId", "userCode" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "canRead", "canWrite" });

            migrationBuilder.CreateIndex(
                name: "UX_SecRight_secRightSeq",
                schema: "admin",
                table: "SecRight",
                column: "secRightSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_registrationStatus",
                schema: "supplier",
                table: "Supplier",
                column: "registrationStatus");

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_seccodeId",
                schema: "supplier",
                table: "Supplier",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "UQ_Supplier_legalName",
                schema: "supplier",
                table: "Supplier",
                column: "legalName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_Supplier_supplierCode",
                schema: "supplier",
                table: "Supplier",
                column: "supplierCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Supplier_supplierSeq",
                schema: "supplier",
                table: "Supplier",
                column: "supplierSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierAddress_supplierId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "supplierId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplierAddress_supplierAddressSeq",
                schema: "supplier",
                table: "SupplierAddress",
                column: "supplierAddressSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_SupplierContact_supplier_email",
                schema: "supplier",
                table: "SupplierContact",
                columns: new[] { "supplierId", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SupplierContact_supplierContactSeq",
                schema: "supplier",
                table: "SupplierContact",
                column: "supplierContactSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierUserMap_appUserId",
                schema: "admin",
                table: "SupplierUserMap",
                column: "appUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierUserMap_secRightId",
                schema: "admin",
                table: "SupplierUserMap",
                column: "secRightId");

            migrationBuilder.CreateIndex(
                name: "UQ_SupplierUserMap_supplier_user",
                schema: "admin",
                table: "SupplierUserMap",
                columns: new[] { "supplierId", "appUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SupplierUserMap_supplierUserMapSeq",
                schema: "admin",
                table: "SupplierUserMap",
                column: "supplierUserMapSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierVerification_seccodeId",
                schema: "supplier",
                table: "SupplierVerification",
                column: "seccodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierVerification_supplier_type",
                schema: "supplier",
                table: "SupplierVerification",
                columns: new[] { "supplierId", "verificationType", "attemptedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "UX_SupplierVerification_supplierVerificationSeq",
                schema: "supplier",
                table: "SupplierVerification",
                column: "supplierVerificationSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_Tenant_tenantSeq",
                schema: "admin",
                table: "Tenant",
                column: "tenantSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRole_roleId",
                schema: "admin",
                table: "UserRole",
                column: "roleId");

            migrationBuilder.CreateIndex(
                name: "UQ_UserRole_user_role",
                schema: "admin",
                table: "UserRole",
                columns: new[] { "appUserId", "roleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_UserRole_userRoleSeq",
                schema: "admin",
                table: "UserRole",
                column: "userRoleSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AsnLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "CommunicationMessage",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "CreditDebitNote",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "DeliverySchedule",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "DocumentUpload",
                schema: "doc");

            migrationBuilder.DropTable(
                name: "GoodsReceipt",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "InforEndpointMap",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "IntegrationError",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "InvoiceLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "Payment",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "RolePermission",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "SupplierAddress",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "SupplierContact",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "SupplierUserMap",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "SupplierVerification",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "Tenant",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "UserRole",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "InforSyncLog",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLine",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "Invoice",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "Permission",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "SecRight",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Supplier",
                schema: "supplier");

            migrationBuilder.DropTable(
                name: "Role",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "Asn",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "PurchaseOrder",
                schema: "proc");

            migrationBuilder.DropTable(
                name: "Seccode",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "AppUser",
                schema: "admin");
        }
    }
}
