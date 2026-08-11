using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 2026-08-11 — one live Create and one live Delete per document. Two dispatcher instances draining the same
    /// outbox could each seed the same document on the same poll and push two items to IDM (observed: two Create
    /// rows 76 ms apart, both dispatched). The seed scan's in-SQL dedupe becomes a durable constraint here; the
    /// scan swallows the resulting unique violation and re-derives on the next poll.
    /// </summary>
    public partial class _0061_IdmOutboxUniqueLiveCreateDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dedupe BEFORE the index — it cannot be created over existing duplicates. Survivor per
            // (document, operation): the row carrying a pid (it is the one stamped on the document), else the
            // furthest-progressed status, else the oldest. Losers are REAPED (soft-deleted), never hard-deleted:
            // a loser may have pushed a real IDM item and its request/response snapshot is the only record of it.
            // QUOTED_IDENTIFIER must be ON for the filtered index below — EF/SqlClient sets it, but a generated
            // script piped through sqlcmd (default OFF) would fail without this, so set it explicitly.
            migrationBuilder.Sql("SET QUOTED_IDENTIFIER ON;");
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT idmDocumentOutboxId,
                           ROW_NUMBER() OVER (
                               PARTITION BY documentUploadId, operation
                               ORDER BY CASE WHEN externalId IS NOT NULL THEN 0 ELSE 1 END,
                                        CASE status WHEN 'Success' THEN 0 WHEN 'InFlight' THEN 1 WHEN 'Pending' THEN 2
                                                    WHEN 'Blocked' THEN 3 WHEN 'Failed' THEN 4 ELSE 5 END,
                                        idmDocumentOutboxSeq) AS rn
                    FROM integration.IdmDocumentOutbox
                    WHERE isDeleted = 0 AND operation IN ('Create', 'Delete'))
                UPDATE o
                   SET isDeleted = 1, deletedOn = SYSUTCDATETIME(), deletedBy = 'migration-0061-dedupe'
                  FROM integration.IdmDocumentOutbox o
                  JOIN ranked r ON r.idmDocumentOutboxId = o.idmDocumentOutboxId
                 WHERE r.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "UQ_IdmDocumentOutbox_documentUploadId_operation_live",
                schema: "integration",
                table: "IdmDocumentOutbox",
                columns: new[] { "documentUploadId", "operation" },
                unique: true,
                filter: "[isDeleted] = 0 AND [operation] IN ('Create', 'Delete')");
        }

        /// <summary>Drops the index only — the dedupe is NOT reversed: un-reaping the losers would restore a state
        /// this index forbids, and they are duplicates by definition.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_IdmDocumentOutbox_documentUploadId_operation_live",
                schema: "integration",
                table: "IdmDocumentOutbox");
        }
    }
}
