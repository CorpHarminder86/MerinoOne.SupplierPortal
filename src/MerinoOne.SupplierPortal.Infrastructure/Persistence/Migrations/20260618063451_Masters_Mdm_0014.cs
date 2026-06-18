using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Masters_Mdm_0014 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_Item_code",
                schema: "inv",
                table: "Item");

            migrationBuilder.EnsureSchema(
                name: "mdm");

            migrationBuilder.AddColumn<Guid>(
                name: "cityId",
                schema: "supplier",
                table: "SupplierAddress",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "countryId",
                schema: "supplier",
                table: "SupplierAddress",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "postalCodeId",
                schema: "supplier",
                table: "SupplierAddress",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "stateId",
                schema: "supplier",
                table: "SupplierAddress",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "itemGroupId",
                schema: "inv",
                table: "Item",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantEntityId",
                schema: "inv",
                table: "Item",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenantId",
                schema: "inv",
                table: "Item",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "unitId",
                schema: "inv",
                table: "Item",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Currency",
                schema: "mdm",
                columns: table => new
                {
                    currencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    description = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    isoCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    symbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    decimalPlaces = table.Column<int>(type: "int", nullable: false, defaultValue: 2),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    currencySeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Currency", x => x.currencyId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "ItemGroup",
                schema: "inv",
                columns: table => new
                {
                    itemGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    itemGroupSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_ItemGroup", x => x.itemGroupId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Unit",
                schema: "mdm",
                columns: table => new
                {
                    unitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    tenantEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    unitType = table.Column<int>(type: "int", nullable: false),
                    isoCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    decimalPlaces = table.Column<int>(type: "int", nullable: false, defaultValue: 2),
                    conversionFactor = table.Column<decimal>(type: "decimal(18,6)", nullable: false, defaultValue: 1m),
                    baseUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    unitSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Unit", x => x.unitId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Unit_Unit_baseUnitId",
                        column: x => x.baseUnitId,
                        principalSchema: "mdm",
                        principalTable: "Unit",
                        principalColumn: "unitId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Country",
                schema: "mdm",
                columns: table => new
                {
                    countryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    description = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    isoCode2 = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    isoCode3 = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    telephoneCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    currencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    countrySeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_Country", x => x.countryId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Country_Currency_currencyId",
                        column: x => x.currencyId,
                        principalSchema: "mdm",
                        principalTable: "Currency",
                        principalColumn: "currencyId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "State",
                schema: "mdm",
                columns: table => new
                {
                    stateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    countryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    isoCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    stateSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_State", x => x.stateId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_State_Country_countryId",
                        column: x => x.countryId,
                        principalSchema: "mdm",
                        principalTable: "Country",
                        principalColumn: "countryId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "City",
                schema: "mdm",
                columns: table => new
                {
                    cityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    countryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    stateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    citySeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_City", x => x.cityId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_City_Country_countryId",
                        column: x => x.countryId,
                        principalSchema: "mdm",
                        principalTable: "Country",
                        principalColumn: "countryId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_City_State_stateId",
                        column: x => x.stateId,
                        principalSchema: "mdm",
                        principalTable: "State",
                        principalColumn: "stateId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostalCode",
                schema: "mdm",
                columns: table => new
                {
                    postalCodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    area = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    countryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    stateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    cityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    postalCodeSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_PostalCode", x => x.postalCodeId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_PostalCode_City_cityId",
                        column: x => x.cityId,
                        principalSchema: "mdm",
                        principalTable: "City",
                        principalColumn: "cityId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostalCode_Country_countryId",
                        column: x => x.countryId,
                        principalSchema: "mdm",
                        principalTable: "Country",
                        principalColumn: "countryId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostalCode_State_stateId",
                        column: x => x.stateId,
                        principalSchema: "mdm",
                        principalTable: "State",
                        principalColumn: "stateId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierAddress_cityId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "cityId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierAddress_countryId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "countryId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierAddress_postalCodeId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "postalCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierAddress_stateId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "stateId");

            migrationBuilder.CreateIndex(
                name: "IX_Item_itemGroupId",
                schema: "inv",
                table: "Item",
                column: "itemGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Item_tenant_company",
                schema: "inv",
                table: "Item",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Item_unitId",
                schema: "inv",
                table: "Item",
                column: "unitId");

            migrationBuilder.CreateIndex(
                name: "UQ_Item_company_code",
                schema: "inv",
                table: "Item",
                columns: new[] { "tenantEntityId", "code" },
                unique: true,
                filter: "[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_City_countryId",
                schema: "mdm",
                table: "City",
                column: "countryId");

            migrationBuilder.CreateIndex(
                name: "IX_City_stateId",
                schema: "mdm",
                table: "City",
                column: "stateId");

            migrationBuilder.CreateIndex(
                name: "IX_City_tenant_description",
                schema: "mdm",
                table: "City",
                columns: new[] { "tenantId", "description" });

            migrationBuilder.CreateIndex(
                name: "UQ_City_tenant_code",
                schema: "mdm",
                table: "City",
                columns: new[] { "tenantId", "code" },
                unique: true,
                filter: "[tenantId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_City_citySeq",
                schema: "mdm",
                table: "City",
                column: "citySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Country_currencyId",
                schema: "mdm",
                table: "Country",
                column: "currencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Country_tenant_description",
                schema: "mdm",
                table: "Country",
                columns: new[] { "tenantId", "description" });

            migrationBuilder.CreateIndex(
                name: "UQ_Country_tenant_code",
                schema: "mdm",
                table: "Country",
                columns: new[] { "tenantId", "code" },
                unique: true,
                filter: "[tenantId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Country_countrySeq",
                schema: "mdm",
                table: "Country",
                column: "countrySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Currency_tenant_description",
                schema: "mdm",
                table: "Currency",
                columns: new[] { "tenantId", "description" });

            migrationBuilder.CreateIndex(
                name: "UQ_Currency_tenant_code",
                schema: "mdm",
                table: "Currency",
                columns: new[] { "tenantId", "code" },
                unique: true,
                filter: "[tenantId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Currency_currencySeq",
                schema: "mdm",
                table: "Currency",
                column: "currencySeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemGroup_tenant_company",
                schema: "inv",
                table: "ItemGroup",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_ItemGroup_company_code",
                schema: "inv",
                table: "ItemGroup",
                columns: new[] { "tenantEntityId", "code" },
                unique: true,
                filter: "[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_ItemGroup_itemGroupSeq",
                schema: "inv",
                table: "ItemGroup",
                column: "itemGroupSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_PostalCode_cityId",
                schema: "mdm",
                table: "PostalCode",
                column: "cityId");

            migrationBuilder.CreateIndex(
                name: "IX_PostalCode_countryId",
                schema: "mdm",
                table: "PostalCode",
                column: "countryId");

            migrationBuilder.CreateIndex(
                name: "IX_PostalCode_stateId",
                schema: "mdm",
                table: "PostalCode",
                column: "stateId");

            migrationBuilder.CreateIndex(
                name: "UQ_PostalCode_tenant_code",
                schema: "mdm",
                table: "PostalCode",
                columns: new[] { "tenantId", "code" },
                unique: true,
                filter: "[tenantId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_PostalCode_postalCodeSeq",
                schema: "mdm",
                table: "PostalCode",
                column: "postalCodeSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_State_countryId",
                schema: "mdm",
                table: "State",
                column: "countryId");

            migrationBuilder.CreateIndex(
                name: "IX_State_tenant_description",
                schema: "mdm",
                table: "State",
                columns: new[] { "tenantId", "description" });

            migrationBuilder.CreateIndex(
                name: "UQ_State_tenant_code",
                schema: "mdm",
                table: "State",
                columns: new[] { "tenantId", "code" },
                unique: true,
                filter: "[tenantId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_State_stateSeq",
                schema: "mdm",
                table: "State",
                column: "stateSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Unit_baseUnitId",
                schema: "mdm",
                table: "Unit",
                column: "baseUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Unit_tenant_company",
                schema: "mdm",
                table: "Unit",
                columns: new[] { "tenantId", "tenantEntityId" });

            migrationBuilder.CreateIndex(
                name: "UQ_Unit_company_code",
                schema: "mdm",
                table: "Unit",
                columns: new[] { "tenantEntityId", "code" },
                unique: true,
                filter: "[tenantEntityId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Unit_unitSeq",
                schema: "mdm",
                table: "Unit",
                column: "unitSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_Item_ItemGroup_itemGroupId",
                schema: "inv",
                table: "Item",
                column: "itemGroupId",
                principalSchema: "inv",
                principalTable: "ItemGroup",
                principalColumn: "itemGroupId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Item_Unit_unitId",
                schema: "inv",
                table: "Item",
                column: "unitId",
                principalSchema: "mdm",
                principalTable: "Unit",
                principalColumn: "unitId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierAddress_City_cityId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "cityId",
                principalSchema: "mdm",
                principalTable: "City",
                principalColumn: "cityId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierAddress_Country_countryId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "countryId",
                principalSchema: "mdm",
                principalTable: "Country",
                principalColumn: "countryId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierAddress_PostalCode_postalCodeId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "postalCodeId",
                principalSchema: "mdm",
                principalTable: "PostalCode",
                principalColumn: "postalCodeId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SupplierAddress_State_stateId",
                schema: "supplier",
                table: "SupplierAddress",
                column: "stateId",
                principalSchema: "mdm",
                principalTable: "State",
                principalColumn: "stateId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Item_ItemGroup_itemGroupId",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropForeignKey(
                name: "FK_Item_Unit_unitId",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierAddress_City_cityId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierAddress_Country_countryId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierAddress_PostalCode_postalCodeId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropForeignKey(
                name: "FK_SupplierAddress_State_stateId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropTable(
                name: "ItemGroup",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "PostalCode",
                schema: "mdm");

            migrationBuilder.DropTable(
                name: "Unit",
                schema: "mdm");

            migrationBuilder.DropTable(
                name: "City",
                schema: "mdm");

            migrationBuilder.DropTable(
                name: "State",
                schema: "mdm");

            migrationBuilder.DropTable(
                name: "Country",
                schema: "mdm");

            migrationBuilder.DropTable(
                name: "Currency",
                schema: "mdm");

            migrationBuilder.DropIndex(
                name: "IX_SupplierAddress_cityId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropIndex(
                name: "IX_SupplierAddress_countryId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropIndex(
                name: "IX_SupplierAddress_postalCodeId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropIndex(
                name: "IX_SupplierAddress_stateId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropIndex(
                name: "IX_Item_itemGroupId",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropIndex(
                name: "IX_Item_tenant_company",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropIndex(
                name: "IX_Item_unitId",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropIndex(
                name: "UQ_Item_company_code",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "cityId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropColumn(
                name: "countryId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropColumn(
                name: "postalCodeId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropColumn(
                name: "stateId",
                schema: "supplier",
                table: "SupplierAddress");

            migrationBuilder.DropColumn(
                name: "itemGroupId",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "tenantEntityId",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "tenantId",
                schema: "inv",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "unitId",
                schema: "inv",
                table: "Item");

            migrationBuilder.CreateIndex(
                name: "UQ_Item_code",
                schema: "inv",
                table: "Item",
                column: "code",
                unique: true);
        }
    }
}
