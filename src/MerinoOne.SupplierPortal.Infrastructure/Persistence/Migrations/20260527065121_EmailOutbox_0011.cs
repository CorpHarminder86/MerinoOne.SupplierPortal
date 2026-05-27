using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailOutbox_0011 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailOutbox",
                schema: "admin",
                columns: table => new
                {
                    emailOutboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    templateKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    toEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    htmlBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<int>(type: "int", nullable: false),
                    attemptCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    nextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    sentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    lastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    emailOutboxSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_EmailOutbox", x => x.emailOutboxId)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_status_nextAttemptAt",
                schema: "admin",
                table: "EmailOutbox",
                columns: new[] { "status", "nextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "UX_EmailOutbox_emailOutboxSeq",
                schema: "admin",
                table: "EmailOutbox",
                column: "emailOutboxSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailOutbox",
                schema: "admin");
        }
    }
}
