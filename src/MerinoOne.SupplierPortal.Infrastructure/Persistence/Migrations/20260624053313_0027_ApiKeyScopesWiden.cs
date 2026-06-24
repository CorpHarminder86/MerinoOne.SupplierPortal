using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0027_ApiKeyScopesWiden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The scopes CSV is unbounded — it grows with every new inbound endpoint (18 tokens already
            // overflowed the old nvarchar(400) on a mint with all scopes selected, raising 2628 truncation).
            // It is never indexed/filtered, so widen to nvarchar(max). Non-lossy: existing rows fit.
            migrationBuilder.AlterColumn<string>(
                name: "scopes",
                schema: "integration",
                table: "ApiKey",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(400)",
                oldMaxLength: 400);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverting narrows the column; any row whose scopes CSV exceeds 400 chars would fail here.
            // That is the correct, intentional behaviour — Down is only valid against data that predates the widen.
            migrationBuilder.AlterColumn<string>(
                name: "scopes",
                schema: "integration",
                table: "ApiKey",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");
        }
    }
}
