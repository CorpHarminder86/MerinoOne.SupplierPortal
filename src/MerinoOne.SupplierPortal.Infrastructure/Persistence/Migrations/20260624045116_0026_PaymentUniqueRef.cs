using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0026_PaymentUniqueRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FAIL-SAFE: refuse to apply against a non-clean environment. The filtered unique index below would error
            // out anyway on existing duplicates, but THROW here gives an actionable message and guarantees we never
            // silently mutate money data. We do NOT auto-delete/merge — duplicates must be resolved by hand first.
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM [proc].[Payment]
    WHERE [isDeleted] = 0 AND [paymentReference] IS NOT NULL
    GROUP BY [tenantId], [tenantEntityId], [invoiceId], [paymentReference]
    HAVING COUNT(*) > 1
)
    THROW 50000, 'Duplicate Payment rows on (tenantId,tenantEntityId,invoiceId,paymentReference); resolve before applying UX_Payment_tenant_invoice_paymentReference.', 1;");

            migrationBuilder.CreateIndex(
                name: "UX_Payment_tenant_invoice_paymentReference",
                schema: "proc",
                table: "Payment",
                columns: new[] { "tenantId", "tenantEntityId", "invoiceId", "paymentReference" },
                unique: true,
                filter: "[isDeleted] = 0 AND [paymentReference] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Payment_tenant_invoice_paymentReference",
                schema: "proc",
                table: "Payment");
        }
    }
}
