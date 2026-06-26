using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0033_ConfirmationGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----------------------------------------------------------------------------------------------------
            // R4 (2026-06-26) — TSD R4 Addendum §3.4 / D1-D2: PO Confirmation Gate reconciling migration (Phase 2b).
            // The C# model already changed (breaking) in Phase 2a; this migration reconciles the dev DB with ZERO
            // data loss. ALL data migrations run FIRST, BEFORE the destructive schema edits below.
            // ----------------------------------------------------------------------------------------------------

            // (1) Remap the stored PoResponseMode -> PoConfirmationMode enum values. The COLUMN NAME is unchanged
            //     (poResponseMode); only the stored string values move: Manual -> AcceptToShip, Auto -> AutoAccept.
            //     Run BEFORE the AlterColumn default-constraint swap so the remap is independent of the default.
            migrationBuilder.Sql(
                "UPDATE [supplier].[Supplier] SET poResponseMode = 'AcceptToShip' WHERE poResponseMode = 'Manual'; " +
                "UPDATE [supplier].[Supplier] SET poResponseMode = 'AutoAccept'   WHERE poResponseMode = 'Auto';");

            // (2) Retire PoStatus.DateProposed: any residual DateProposed PO (PO000002 in dev) rolls to Released.
            //     poStatus has NO DB CHECK constraint (the C# enum is the guard) so this is a pure data update.
            //     MUST run BEFORE the proposedDeliveryDate DropColumn so the retired flow's data is settled first.
            migrationBuilder.Sql(
                "UPDATE [proc].[PurchaseOrder] SET poStatus = 'Released' WHERE poStatus = 'DateProposed';");

            // (3) Now the EF-generated destructive parts. Drop the retired propose-date column.
            migrationBuilder.DropColumn(
                name: "proposedDeliveryDate",
                schema: "proc",
                table: "PurchaseOrder");

            // (4) Default-value swap on poResponseMode (Manual -> AcceptToShip). EF drops the old auto-named
            //     default constraint and recreates it; only future inserts are affected (existing rows already
            //     remapped in step 1).
            migrationBuilder.AlterColumn<string>(
                name: "poResponseMode",
                schema: "supplier",
                table: "Supplier",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "AcceptToShip",
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldDefaultValue: "Manual");

            // (5) Permission cleanup (data): retire PurchaseOrder.ApproveProposal — the date-only propose/approve
            //     flow is replaced by PO Negotiation. Delete its RolePermission rows first (FK_RolePermission_
            //     Permission_PermissionId is RESTRICT), then the Permission row. Idempotent: guarded by existence,
            //     so re-running (or running where the permission was never seeded) is a no-op. The seeder then
            //     adds PurchaseOrder.OverrideGate.
            migrationBuilder.Sql(
                "DELETE rp FROM [admin].[RolePermission] rp " +
                "INNER JOIN [admin].[Permission] p ON p.permissionId = rp.permissionId " +
                "WHERE p.code = 'PurchaseOrder.ApproveProposal'; " +
                "DELETE FROM [admin].[Permission] WHERE code = 'PurchaseOrder.ApproveProposal';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the default-value swap on poResponseMode (AcceptToShip -> Manual).
            migrationBuilder.AlterColumn<string>(
                name: "poResponseMode",
                schema: "supplier",
                table: "Supplier",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual",
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldDefaultValue: "AcceptToShip");

            // Re-add the retired propose-date column (NULLABLE — the historical values are gone, cannot be
            // reconstructed; existing rows get NULL).
            migrationBuilder.AddColumn<DateTime>(
                name: "proposedDeliveryDate",
                schema: "proc",
                table: "PurchaseOrder",
                type: "datetime2",
                nullable: true);

            // BEST-EFFORT reversal of the value remaps. AcceptToShip -> Manual and AutoAccept -> Auto restores
            // the pre-migration string values for rows that were remapped, BUT this is lossy: rows that were
            // GENUINELY AcceptToShip/AutoAccept post-migration (new mode names) are indistinguishable from the
            // remapped ones and would be wrongly downgraded. The three-valued PoConfirmationMode does not map
            // back cleanly onto the two-valued PoResponseMode (AcknowledgeToShip has NO pre-migration equivalent
            // and is left as-is). Down() is for emergency rollback of an as-yet-unused migration only.
            migrationBuilder.Sql(
                "UPDATE [supplier].[Supplier] SET poResponseMode = 'Manual' WHERE poResponseMode = 'AcceptToShip'; " +
                "UPDATE [supplier].[Supplier] SET poResponseMode = 'Auto'   WHERE poResponseMode = 'AutoAccept';");

            // NOTE: PoStatus.DateProposed -> Released (Up step 2) is NOT reversed — the remapped rows are
            // indistinguishable from genuinely-Released rows, so a blanket reversal would corrupt good data.
            // NOTE: PurchaseOrder.ApproveProposal permission + its role-permission rows (Up step 5) are NOT
            // re-created here — the original permissionId and exact role mappings are not recoverable. Re-seeding
            // (if the catalog is restored) is the supported recovery path. Both are documented best-effort gaps.
        }
    }
}
