using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Restored 2026-07-06: this migration was generated on 2026-07-06 06:19 (IDM Entity Type mapping
    /// rework — owner-entity column + optional attachmentType + re-keyed unique index) but its file was
    /// destroyed by an errant <c>ef migrations remove</c> against a stale assembly before it was committed.
    /// Re-authored by hand with the ORIGINAL migration id so dev databases that already applied it stay
    /// consistent with <c>__EFMigrationsHistory</c>. The [DbContext]/[Migration] attributes live here
    /// (not on a Designer partial) because the Designer was not restored — Designers are gitignored in
    /// this repo anyway; note <c>ef migrations remove</c> of the FOLLOWING migration needs this
    /// migration's Designer and will not work (drop + re-add instead).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260706061911_0046_IdmConfigPortalEntityOptionalAttachment")]
    public partial class _0046_IdmConfigPortalEntityOptionalAttachment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_IdmAttachmentTypeConfig_tenant_attachmentType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig");

            migrationBuilder.AlterColumn<string>(
                name: "attachmentType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<string>(
                name: "ownerEntityType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UQ_IdmAttachmentTypeConfig_tenant_owner_attachmentType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                columns: new[] { "tenantId", "ownerEntityType", "attachmentType" },
                unique: true,
                filter: "[isDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_IdmAttachmentTypeConfig_tenant_owner_attachmentType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig");

            migrationBuilder.DropColumn(
                name: "ownerEntityType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig");

            migrationBuilder.AlterColumn<string>(
                name: "attachmentType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UQ_IdmAttachmentTypeConfig_tenant_attachmentType",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                columns: new[] { "tenantId", "attachmentType" },
                unique: true,
                filter: "[isDeleted] = 0");
        }
    }
}
