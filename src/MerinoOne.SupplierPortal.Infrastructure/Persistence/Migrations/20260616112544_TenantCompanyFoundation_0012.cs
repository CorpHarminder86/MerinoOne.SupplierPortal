using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantCompanyFoundation_0012 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_Supplier_legalName",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "UQ_Supplier_supplierCode",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "UQ_Role_name",
                schema: "admin",
                table: "Role");

            migrationBuilder.DropIndex(
                name: "UQ_PaymentTerm_code",
                schema: "proc",
                table: "PaymentTerm");

            migrationBuilder.DropIndex(
                name: "UX_EmailTemplate_templateKey",
                schema: "admin",
                table: "EmailTemplate");

            migrationBuilder.DropIndex(
                name: "UQ_DeliveryTerm_code",
                schema: "proc",
                table: "DeliveryTerm");

            migrationBuilder.AddColumn<bool>(
                name: "isActive",
                schema: "admin",
                table: "Tenant",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantEntityId",
                schema: "admin",
                table: "SupplierInvite",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "admin",
                table: "SupplierInvite",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "admin",
                table: "Role",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantEntityId",
                schema: "proc",
                table: "PaymentTerm",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "proc",
                table: "PaymentTerm",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "integration",
                table: "IntegrationError",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "integration",
                table: "InforSyncLog",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lastIdempotencyKey",
                schema: "integration",
                table: "InforEndpointMap",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lastMessage",
                schema: "integration",
                table: "InforEndpointMap",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "lastReceivedAt",
                schema: "integration",
                table: "InforEndpointMap",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lastStatus",
                schema: "integration",
                table: "InforEndpointMap",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "receivedCount",
                schema: "integration",
                table: "InforEndpointMap",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "integration",
                table: "InforEndpointMap",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "admin",
                table: "EmailTemplate",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "admin",
                table: "EmailOutbox",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantEntityId",
                schema: "proc",
                table: "DeliveryTerm",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "proc",
                table: "DeliveryTerm",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "admin",
                table: "AppUser",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantEntity",
                schema: "admin",
                columns: table => new
                {
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    tenantEntitySeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_TenantEntity", x => x.tenantEntityId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_TenantEntity_Tenant_TenantId",
                        column: x => x.tenantId,
                        principalSchema: "admin",
                        principalTable: "Tenant",
                        principalColumn: "tenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApiKey",
                schema: "integration",
                columns: table => new
                {
                    apiKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    keyPrefix = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    keyHash = table.Column<string>(type: "char(64)", nullable: false),
                    scopes = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    expiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    lastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    revokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    replacedByApiKeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    apiKeySeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_ApiKey", x => x.apiKeyId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_ApiKey_TenantEntity_TenantEntityId",
                        column: x => x.tenantEntityId,
                        principalSchema: "admin",
                        principalTable: "TenantEntity",
                        principalColumn: "tenantEntityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApiKey_Tenant_TenantId",
                        column: x => x.tenantId,
                        principalSchema: "admin",
                        principalTable: "Tenant",
                        principalColumn: "tenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyShareGroup",
                schema: "integration",
                columns: table => new
                {
                    companyShareGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    endpoint = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    sourceTenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    isEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    companyShareGroupSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_CompanyShareGroup", x => x.companyShareGroupId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_CompanyShareGroup_TenantEntity_SourceTenantEntityId",
                        column: x => x.sourceTenantEntityId,
                        principalSchema: "admin",
                        principalTable: "TenantEntity",
                        principalColumn: "tenantEntityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CompanyShareGroup_Tenant_TenantId",
                        column: x => x.tenantId,
                        principalSchema: "admin",
                        principalTable: "Tenant",
                        principalColumn: "tenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserCompanyMap",
                schema: "admin",
                columns: table => new
                {
                    userCompanyMapId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    appUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    isDefault = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    userCompanyMapSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_UserCompanyMap", x => x.userCompanyMapId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_UserCompanyMap_AppUser_AppUserId",
                        column: x => x.appUserId,
                        principalSchema: "admin",
                        principalTable: "AppUser",
                        principalColumn: "appUserId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserCompanyMap_TenantEntity_TenantEntityId",
                        column: x => x.tenantEntityId,
                        principalSchema: "admin",
                        principalTable: "TenantEntity",
                        principalColumn: "tenantEntityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CompanyShareGroupMember",
                schema: "integration",
                columns: table => new
                {
                    companyShareGroupMemberId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    companyShareGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    memberTenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    endpoint = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    companyShareGroupMemberSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_CompanyShareGroupMember", x => x.companyShareGroupMemberId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_CompanyShareGroupMember_CompanyShareGroup_CompanyShareGroupId",
                        column: x => x.companyShareGroupId,
                        principalSchema: "integration",
                        principalTable: "CompanyShareGroup",
                        principalColumn: "companyShareGroupId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompanyShareGroupMember_TenantEntity_MemberTenantEntityId",
                        column: x => x.memberTenantEntityId,
                        principalSchema: "admin",
                        principalTable: "TenantEntity",
                        principalColumn: "tenantEntityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_Tenant_name",
                schema: "admin",
                table: "Tenant",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvite_tenantEntityId",
                schema: "admin",
                table: "SupplierInvite",
                column: "tenantEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvite_tenantId",
                schema: "admin",
                table: "SupplierInvite",
                column: "tenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_tenant_company",
                schema: "supplier",
                table: "Supplier",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_tenantEntityId",
                schema: "supplier",
                table: "Supplier",
                column: "tenantEntityId");

            migrationBuilder.CreateIndex(
                name: "UQ_Supplier_tenant_legalName",
                schema: "supplier",
                table: "Supplier",
                columns: new[] { "tenantId", "legalName" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UQ_Supplier_tenant_supplierCode",
                schema: "supplier",
                table: "Supplier",
                columns: new[] { "tenantId", "supplierCode" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UQ_Role_tenant_name",
                schema: "admin",
                table: "Role",
                columns: new[] { "tenantId", "name" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_tenant_company",
                schema: "proc",
                table: "PurchaseOrder",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTerm_tenant_company",
                schema: "proc",
                table: "PaymentTerm",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_PaymentTerm_company_code",
                schema: "proc",
                table: "PaymentTerm",
                columns: new[] { "tenantEntityId", "code" },
                unique: true,
                filter: "[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Payment_tenant_company",
                schema: "proc",
                table: "Payment",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_tenant_company",
                schema: "proc",
                table: "Invoice",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_EmailTemplate_tenant_templateKey",
                schema: "admin",
                table: "EmailTemplate",
                columns: new[] { "tenantId", "templateKey" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryTerm_tenant_company",
                schema: "proc",
                table: "DeliveryTerm",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_DeliveryTerm_company_code",
                schema: "proc",
                table: "DeliveryTerm",
                columns: new[] { "tenantEntityId", "code" },
                unique: true,
                filter: "[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Asn_tenant_company",
                schema: "proc",
                table: "Asn",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AppUser_tenantId",
                schema: "admin",
                table: "AppUser",
                column: "tenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKey_isActive",
                schema: "integration",
                table: "ApiKey",
                column: "isActive",
                filter: "[isActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKey_tenantEntityId",
                schema: "integration",
                table: "ApiKey",
                column: "tenantEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKey_tenantId",
                schema: "integration",
                table: "ApiKey",
                column: "tenantId");

            migrationBuilder.CreateIndex(
                name: "UX_ApiKey_apiKeySeq",
                schema: "integration",
                table: "ApiKey",
                column: "apiKeySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_ApiKey_keyPrefix",
                schema: "integration",
                table: "ApiKey",
                column: "keyPrefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyShareGroup_sourceTenantEntityId",
                schema: "integration",
                table: "CompanyShareGroup",
                column: "sourceTenantEntityId");

            migrationBuilder.CreateIndex(
                name: "UQ_CompanyShareGroup_tenant_endpoint_source",
                schema: "integration",
                table: "CompanyShareGroup",
                columns: new[] { "tenantId", "endpoint", "sourceTenantEntityId" },
                unique: true,
                filter: "[tenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CompanyShareGroup_companyShareGroupSeq",
                schema: "integration",
                table: "CompanyShareGroup",
                column: "companyShareGroupSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyShareGroupMember_memberTenantEntityId",
                schema: "integration",
                table: "CompanyShareGroupMember",
                column: "memberTenantEntityId");

            migrationBuilder.CreateIndex(
                name: "UQ_CompanyShareGroupMember_endpoint_member",
                schema: "integration",
                table: "CompanyShareGroupMember",
                columns: new[] { "endpoint", "memberTenantEntityId" },
                unique: true,
                filter: "[isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UQ_CompanyShareGroupMember_group_member",
                schema: "integration",
                table: "CompanyShareGroupMember",
                columns: new[] { "companyShareGroupId", "memberTenantEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CompanyShareGroupMember_companyShareGroupMemberSeq",
                schema: "integration",
                table: "CompanyShareGroupMember",
                column: "companyShareGroupMemberSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UQ_TenantEntity_tenant_code",
                schema: "admin",
                table: "TenantEntity",
                columns: new[] { "tenantId", "code" },
                unique: true,
                filter: "[tenantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_TenantEntity_tenantEntitySeq",
                schema: "admin",
                table: "TenantEntity",
                column: "tenantEntitySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMap_appUserId",
                schema: "admin",
                table: "UserCompanyMap",
                column: "appUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMap_tenantEntityId",
                schema: "admin",
                table: "UserCompanyMap",
                column: "tenantEntityId");

            migrationBuilder.CreateIndex(
                name: "UQ_UserCompanyMap_user_company",
                schema: "admin",
                table: "UserCompanyMap",
                columns: new[] { "appUserId", "tenantEntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_UserCompanyMap_userCompanyMapSeq",
                schema: "admin",
                table: "UserCompanyMap",
                column: "userCompanyMapSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_AppUser_Tenant_TenantId",
                schema: "admin",
                table: "AppUser",
                column: "tenantId",
                principalSchema: "admin",
                principalTable: "Tenant",
                principalColumn: "tenantId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Supplier_TenantEntity_TenantEntityId",
                schema: "supplier",
                table: "Supplier",
                column: "tenantEntityId",
                principalSchema: "admin",
                principalTable: "TenantEntity",
                principalColumn: "tenantEntityId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierInvite_TenantEntity_TenantEntityId",
                schema: "admin",
                table: "SupplierInvite",
                column: "tenantEntityId",
                principalSchema: "admin",
                principalTable: "TenantEntity",
                principalColumn: "tenantEntityId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierInvite_Tenant_TenantId",
                schema: "admin",
                table: "SupplierInvite",
                column: "tenantId",
                principalSchema: "admin",
                principalTable: "Tenant",
                principalColumn: "tenantId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppUser_Tenant_TenantId",
                schema: "admin",
                table: "AppUser");

            migrationBuilder.DropForeignKey(
                name: "FK_Supplier_TenantEntity_TenantEntityId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierInvite_TenantEntity_TenantEntityId",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierInvite_Tenant_TenantId",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropTable(
                name: "ApiKey",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "CompanyShareGroupMember",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "UserCompanyMap",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "CompanyShareGroup",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "TenantEntity",
                schema: "admin");

            migrationBuilder.DropIndex(
                name: "UQ_Tenant_name",
                schema: "admin",
                table: "Tenant");

            migrationBuilder.DropIndex(
                name: "IX_SupplierInvite_tenantEntityId",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropIndex(
                name: "IX_SupplierInvite_tenantId",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropIndex(
                name: "IX_Supplier_tenant_company",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "IX_Supplier_tenantEntityId",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "UQ_Supplier_tenant_legalName",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "UQ_Supplier_tenant_supplierCode",
                schema: "supplier",
                table: "Supplier");

            migrationBuilder.DropIndex(
                name: "UQ_Role_tenant_name",
                schema: "admin",
                table: "Role");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrder_tenant_company",
                schema: "proc",
                table: "PurchaseOrder");

            migrationBuilder.DropIndex(
                name: "IX_PaymentTerm_tenant_company",
                schema: "proc",
                table: "PaymentTerm");

            migrationBuilder.DropIndex(
                name: "UQ_PaymentTerm_company_code",
                schema: "proc",
                table: "PaymentTerm");

            migrationBuilder.DropIndex(
                name: "IX_Payment_tenant_company",
                schema: "proc",
                table: "Payment");

            migrationBuilder.DropIndex(
                name: "IX_Invoice_tenant_company",
                schema: "proc",
                table: "Invoice");

            migrationBuilder.DropIndex(
                name: "UX_EmailTemplate_tenant_templateKey",
                schema: "admin",
                table: "EmailTemplate");

            migrationBuilder.DropIndex(
                name: "IX_DeliveryTerm_tenant_company",
                schema: "proc",
                table: "DeliveryTerm");

            migrationBuilder.DropIndex(
                name: "UQ_DeliveryTerm_company_code",
                schema: "proc",
                table: "DeliveryTerm");

            migrationBuilder.DropIndex(
                name: "IX_Asn_tenant_company",
                schema: "proc",
                table: "Asn");

            migrationBuilder.DropIndex(
                name: "IX_AppUser_tenantId",
                schema: "admin",
                table: "AppUser");

            migrationBuilder.DropColumn(
                name: "isActive",
                schema: "admin",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "tenantEntityId",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "admin",
                table: "SupplierInvite");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "admin",
                table: "Role");

            migrationBuilder.DropColumn(
                name: "tenantEntityId",
                schema: "proc",
                table: "PaymentTerm");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "proc",
                table: "PaymentTerm");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "integration",
                table: "IntegrationError");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "integration",
                table: "InforSyncLog");

            migrationBuilder.DropColumn(
                name: "lastIdempotencyKey",
                schema: "integration",
                table: "InforEndpointMap");

            migrationBuilder.DropColumn(
                name: "lastMessage",
                schema: "integration",
                table: "InforEndpointMap");

            migrationBuilder.DropColumn(
                name: "lastReceivedAt",
                schema: "integration",
                table: "InforEndpointMap");

            migrationBuilder.DropColumn(
                name: "lastStatus",
                schema: "integration",
                table: "InforEndpointMap");

            migrationBuilder.DropColumn(
                name: "receivedCount",
                schema: "integration",
                table: "InforEndpointMap");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "integration",
                table: "InforEndpointMap");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "admin",
                table: "EmailTemplate");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "admin",
                table: "EmailOutbox");

            migrationBuilder.DropColumn(
                name: "tenantEntityId",
                schema: "proc",
                table: "DeliveryTerm");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "proc",
                table: "DeliveryTerm");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "admin",
                table: "AppUser");

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
                name: "UQ_Role_name",
                schema: "admin",
                table: "Role",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_PaymentTerm_code",
                schema: "proc",
                table: "PaymentTerm",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_EmailTemplate_templateKey",
                schema: "admin",
                table: "EmailTemplate",
                column: "templateKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_DeliveryTerm_code",
                schema: "proc",
                table: "DeliveryTerm",
                column: "code",
                unique: true);
        }
    }
}
