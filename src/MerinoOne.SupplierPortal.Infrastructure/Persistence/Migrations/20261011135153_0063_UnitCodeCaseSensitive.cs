using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 2026-10-11 — unit codes become CASE-SENSITIVE: "PCS" and "pcs" are two different units. mdm.Unit.code moves from the
    /// database collation (SQL_Latin1_General_CP1_CI_AS, under which the two collide in UQ_Unit_company_code) to
    /// SQL_Latin1_General_CP1_CS_AS.
    ///
    /// SQL Server cannot ALTER the collation of a column that sits in an index, so EF's SQL Server generator brackets the
    /// ALTER COLUMN with DROP INDEX / CREATE UNIQUE INDEX for UQ_Unit_company_code (same name, columns and filter) in BOTH Up
    /// and Down. That bracket exists only in the generated SQL (see `dotnet ef migrations script`), so it is not repeated here as
    /// DropIndex/CreateIndex operations. Up cannot fail on existing data: a set that was unique case-insensitively is unique
    /// case-sensitively. Down restores the database default collation and CAN fail on its unique index if two units that differ
    /// only by case exist in one company by then — rename or remove one first.
    /// </summary>
    public partial class _0063_UnitCodeCaseSensitive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // UQ_Unit_company_code is a FILTERED unique index and EF's generated SQL re-creates it right after the ALTER COLUMN.
            // A script piped through sqlcmd runs with QUOTED_IDENTIFIER OFF (EF/SqlClient default to ON) and would fail there,
            // so set it explicitly — the same guard as migration 0061.
            migrationBuilder.Sql("SET QUOTED_IDENTIFIER ON;");
            migrationBuilder.AlterColumn<string>(
                name: "code",
                schema: "mdm",
                table: "Unit",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                collation: "SQL_Latin1_General_CP1_CS_AS",
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // UQ_Unit_company_code is a FILTERED unique index and EF's generated SQL re-creates it right after the ALTER COLUMN.
            // A script piped through sqlcmd runs with QUOTED_IDENTIFIER OFF (EF/SqlClient default to ON) and would fail there,
            // so set it explicitly — the same guard as migration 0061.
            migrationBuilder.Sql("SET QUOTED_IDENTIFIER ON;");
            migrationBuilder.AlterColumn<string>(
                name: "code",
                schema: "mdm",
                table: "Unit",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldCollation: "SQL_Latin1_General_CP1_CS_AS");
        }
    }
}
