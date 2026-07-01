using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0041_RolePermGuardrails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserRole_roleId",
                schema: "admin",
                table: "UserRole");

            migrationBuilder.DropIndex(
                name: "UQ_RolePermission_role_permission",
                schema: "admin",
                table: "RolePermission");

            migrationBuilder.CreateIndex(
                name: "IX_UserRole_role_user",
                schema: "admin",
                table: "UserRole",
                columns: new[] { "roleId", "appUserId" });

            migrationBuilder.CreateIndex(
                name: "UQ_RolePermission_role_permission",
                schema: "admin",
                table: "RolePermission",
                columns: new[] { "roleId", "permissionId" },
                unique: true,
                filter: "[isDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserRole_role_user",
                schema: "admin",
                table: "UserRole");

            migrationBuilder.DropIndex(
                name: "UQ_RolePermission_role_permission",
                schema: "admin",
                table: "RolePermission");

            migrationBuilder.CreateIndex(
                name: "IX_UserRole_roleId",
                schema: "admin",
                table: "UserRole",
                column: "roleId");

            migrationBuilder.CreateIndex(
                name: "UQ_RolePermission_role_permission",
                schema: "admin",
                table: "RolePermission",
                columns: new[] { "roleId", "permissionId" },
                unique: true);
        }
    }
}
