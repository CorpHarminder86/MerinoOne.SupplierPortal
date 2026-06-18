using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InforConnectionSettings_0015 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InforConnectionSetting",
                schema: "integration",
                columns: table => new
                {
                    inforConnectionSettingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    tenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    accessTokenUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    clientId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    clientSecret = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    username = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    password = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    apiBaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ionC4wsBaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    company = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    inforConnectionSettingSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_InforConnectionSetting", x => x.inforConnectionSettingId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "UQ_InforConnectionSetting_tenantId",
                schema: "integration",
                table: "InforConnectionSetting",
                column: "tenantId",
                unique: true,
                filter: "[tenantId] IS NOT NULL AND [isDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_InforConnectionSetting_inforConnectionSettingSeq",
                schema: "integration",
                table: "InforConnectionSetting",
                column: "inforConnectionSettingSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InforConnectionSetting",
                schema: "integration");
        }
    }
}
