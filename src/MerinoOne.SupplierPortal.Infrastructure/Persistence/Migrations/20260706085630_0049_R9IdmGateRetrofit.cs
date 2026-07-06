using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// R9 (TSD R9 §2.11, D-R9-16) — the R8 IDM gate retrofit: the dot-path-array column becomes the
    /// JSONata expression column, and every stored JSON array converts IN PLACE to the equivalent
    /// null-safe conjunction — scripted, no admin re-entry. Per-path term
    /// <c>(p != null and $trim($string(p)) != "")</c> reproduces the old IsNullOrWhiteSpace semantics
    /// exactly under the strict-true engine (the C# mirror is IdmGateConversion; verdict equivalence is
    /// pinned by IdmEligibilityGateTests). Rows with malformed legacy JSON are left unconverted — the
    /// engine fails closed on them, identical to the old gate's malformed-JSON behaviour. Down restores
    /// only the column name (the conversion is one-way; a converted expression still fails closed under
    /// the old JsonPath gate rather than misfiring).
    /// </summary>
    public partial class _0049_R9IdmGateRetrofit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "eligibilityGateJson",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                newName: "eligibilityGateExpr");

            // Convert stored dot-path arrays → JSONata conjunctions (SQL equivalent of IdmGateConversion).
            migrationBuilder.Sql(@"
UPDATE c SET eligibilityGateExpr = conv.expr
FROM [integration].[IdmAttachmentTypeConfig] c
CROSS APPLY (
    SELECT STRING_AGG(
               '(' + LTRIM(RTRIM(j.[value])) + ' != null and $trim($string(' + LTRIM(RTRIM(j.[value])) + ')) != """")',
               ' and ') WITHIN GROUP (ORDER BY CAST(j.[key] AS INT)) AS expr
    FROM OPENJSON(c.eligibilityGateExpr) j
    WHERE LTRIM(RTRIM(j.[value])) <> ''
) conv
WHERE ISJSON(c.eligibilityGateExpr) = 1
  AND LEFT(LTRIM(c.eligibilityGateExpr), 1) = '['
  AND conv.expr IS NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "eligibilityGateExpr",
                schema: "integration",
                table: "IdmAttachmentTypeConfig",
                newName: "eligibilityGateJson");
        }
    }
}
