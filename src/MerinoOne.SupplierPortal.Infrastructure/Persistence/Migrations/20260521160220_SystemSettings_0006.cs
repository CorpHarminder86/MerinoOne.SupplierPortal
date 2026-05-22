using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemSettings_0006 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "settings");

            migrationBuilder.CreateTable(
                name: "SystemSetting",
                schema: "settings",
                columns: table => new
                {
                    systemSettingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    category = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    settingKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    settingValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    systemSettingSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_SystemSetting", x => x.systemSettingId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SystemSetting_category",
                schema: "settings",
                table: "SystemSetting",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "UQ_SystemSetting_category_settingKey",
                schema: "settings",
                table: "SystemSetting",
                columns: new[] { "category", "settingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SystemSetting_systemSettingSeq",
                schema: "settings",
                table: "SystemSetting",
                column: "systemSettingSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            // Idempotent seed for EmailConfig (7 keys) + SupplierInvite (1 key).
            migrationBuilder.Sql(@"
INSERT INTO settings.SystemSetting (systemSettingId, category, settingKey, settingValue, description, isActive, createdBy, createdOn, isDeleted)
SELECT NEWID(), v.category, v.k, v.val, v.d, 1, 'system', SYSUTCDATETIME(), 0
FROM (VALUES
 ('EmailConfig','Email','','From-address for outbound system mail.'),
 ('EmailConfig','Host','','SMTP server hostname (e.g. smtp.office365.com).'),
 ('EmailConfig','Port','587','SMTP port. 587 (STARTTLS) or 465 (SSL).'),
 ('EmailConfig','EnableSsl','true','Use TLS/SSL when connecting to SMTP server.'),
 ('EmailConfig','UserName','','SMTP username when DefaultCredentials=false.'),
 ('EmailConfig','Password','','SMTP password (encrypted via DataProtection).'),
 ('EmailConfig','DefaultCredentials','true','Use network default credentials instead of UserName/Password.'),
 ('SupplierInvite','ExpiryDays','7','Days before a supplier invite token expires.')
) AS v(category, k, val, d)
WHERE NOT EXISTS (
    SELECT 1 FROM settings.SystemSetting x
    WHERE x.category = v.category AND x.settingKey = v.k AND x.isDeleted = 0
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemSetting",
                schema: "settings");
        }
    }
}
