using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OtpTables_0008 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InviteOtp",
                schema: "admin",
                columns: table => new
                {
                    inviteOtpId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    supplierInviteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    codeHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    issuedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    expiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    consumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    inviteOtpSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_InviteOtp", x => x.inviteOtpId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_InviteOtp_SupplierInvite_SupplierInviteId",
                        column: x => x.supplierInviteId,
                        principalSchema: "admin",
                        principalTable: "SupplierInvite",
                        principalColumn: "supplierInviteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoginOtp",
                schema: "admin",
                columns: table => new
                {
                    loginOtpId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    appUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    codeHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    issuedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    expiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    consumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    mfaToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    loginOtpSeq = table.Column<int>(type: "int", nullable: false)
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
                    table.PrimaryKey("PK_LoginOtp", x => x.loginOtpId)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_LoginOtp_AppUser_AppUserId",
                        column: x => x.appUserId,
                        principalSchema: "admin",
                        principalTable: "AppUser",
                        principalColumn: "appUserId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InviteOtp_supplierInviteId_issuedAt",
                schema: "admin",
                table: "InviteOtp",
                columns: new[] { "supplierInviteId", "issuedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "UX_InviteOtp_inviteOtpSeq",
                schema: "admin",
                table: "InviteOtp",
                column: "inviteOtpSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginOtp_appUserId_issuedAt",
                schema: "admin",
                table: "LoginOtp",
                columns: new[] { "appUserId", "issuedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "UX_LoginOtp_loginOtpSeq",
                schema: "admin",
                table: "LoginOtp",
                column: "loginOtpSeq",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_LoginOtp_mfaToken",
                schema: "admin",
                table: "LoginOtp",
                column: "mfaToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InviteOtp",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "LoginOtp",
                schema: "admin");
        }
    }
}
