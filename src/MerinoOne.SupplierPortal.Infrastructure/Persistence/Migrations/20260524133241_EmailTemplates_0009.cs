using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailTemplates_0009 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailTemplate",
                schema: "admin",
                columns: table => new
                {
                    emailTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    templateKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    htmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    isActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    emailTemplateSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_EmailTemplate", x => x.emailTemplateId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "UX_EmailTemplate_emailTemplateSeq",
                schema: "admin",
                table: "EmailTemplate",
                column: "emailTemplateSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_EmailTemplate_templateKey",
                schema: "admin",
                table: "EmailTemplate",
                column: "templateKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailTemplate",
                schema: "admin");
        }
    }
}
