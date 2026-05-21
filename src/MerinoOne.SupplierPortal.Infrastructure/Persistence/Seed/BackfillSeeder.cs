using System.Data;
using Dapper;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

public static class BackfillSeeder
{
    /// <summary>
    /// Per-supplier counts. Supplier index 0 (S0001) gets the heavy invoice load (50K),
    /// the rest get 5K each — keeps total under SqlExpress 10GB cap.
    /// </summary>
    public record SupplierVolumes(int Pos, int InvoicesPerSupplier, int Asns, int Grns, int Schedules, int Payments)
    {
        public static SupplierVolumes ForIndex(int i) => i == 0
            ? new SupplierVolumes(10_000, 50_000, 10_000, 10_000, 10_000, 5_000)
            : new SupplierVolumes(10_000,  5_000, 10_000, 10_000, 10_000, 5_000);
    }

    public static async Task SeedAsync(AppDbContext ctx, string connectionString, CancellationToken ct = default)
    {
        var suppliers = await ctx.Suppliers.IgnoreQueryFilters()
            .OrderBy(s => s.SupplierCode)
            .Select(s => new { s.Id, s.SupplierCode, s.SeccodeId })
            .ToListAsync(ct);

        for (int i = 0; i < suppliers.Count; i++)
        {
            var sup = suppliers[i];
            var vols = SupplierVolumes.ForIndex(i);
            await SeedOneSupplierAsync(connectionString, sup.Id, sup.SupplierCode, sup.SeccodeId, vols, ct);
        }
    }

    private static async Task SeedOneSupplierAsync(string cs, Guid supplierId, string supplierCode, Guid seccodeId, SupplierVolumes vols, CancellationToken ct)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);

        // idempotency check
        var existing = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM [proc].[PurchaseOrder] WHERE supplierId=@id",
            new { id = supplierId });
        if (existing > 0) { Console.WriteLine($"[backfill] {supplierCode}: already seeded, skipping"); return; }

        Console.WriteLine($"[backfill] {supplierCode}: starting ({vols.Pos} POs, {vols.InvoicesPerSupplier} invoices)...");

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // POs
            var poIds = new Guid[vols.Pos];
            for (int i = 0; i < vols.Pos; i++)
                poIds[i] = DeterministicId.From("PO", $"{supplierCode}|{i:D6}");

            using (var dt = BuildPoTable(supplierId, supplierCode, seccodeId, poIds))
            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, tx)
            {
                DestinationTableName = "[proc].[PurchaseOrder]",
                BatchSize = 5000,
                BulkCopyTimeout = 600
            })
            {
                MapColumns(bulk, dt);
                await bulk.WriteToServerAsync(dt, ct);
            }

            // PO lines via INSERT…SELECT (3 lines per PO)
            await conn.ExecuteAsync(@"
                INSERT INTO [proc].[PurchaseOrderLine]
                  (purchaseOrderLineId, purchaseOrderId, positionNo, sequenceNo, itemCode, itemDescription,
                   orderUnit, orderQty, priceUnit, price, discountPct, discountAmount,
                   createdOn, createdBy, isDeleted)
                SELECT NEWID(), p.purchaseOrderId, n.n, n.n,
                       CONCAT('ITM-', RIGHT('00000' + CAST(n.n AS varchar(5)), 5)),
                       'Sample item ' + CAST(n.n AS varchar(5)),
                       'EA', 100.0, 50.0, 5000.0, 0.0, 0.0,
                       SYSUTCDATETIME(), 'seed', 0
                FROM [proc].[PurchaseOrder] p
                CROSS APPLY (VALUES (1),(2),(3)) AS n(n)
                WHERE p.supplierId=@sid",
                new { sid = supplierId }, tx, commandTimeout: 600);

            // ASNs
            var asnIds = new Guid[vols.Asns];
            for (int i = 0; i < vols.Asns; i++)
                asnIds[i] = DeterministicId.From("ASN", $"{supplierCode}|{i:D6}");
            using (var dt = BuildAsnTable(supplierId, supplierCode, seccodeId, asnIds, poIds))
            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, tx)
            {
                DestinationTableName = "[proc].[Asn]",
                BatchSize = 5000,
                BulkCopyTimeout = 600
            })
            {
                MapColumns(bulk, dt);
                await bulk.WriteToServerAsync(dt, ct);
            }

            // DeliverySchedules
            var dsIds = new Guid[vols.Schedules];
            for (int i = 0; i < vols.Schedules; i++)
                dsIds[i] = DeterministicId.From("DS", $"{supplierCode}|{i:D6}");
            using (var dt = BuildDeliveryScheduleTable(seccodeId, dsIds, poIds))
            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, tx)
            {
                DestinationTableName = "[proc].[DeliverySchedule]",
                BatchSize = 5000,
                BulkCopyTimeout = 600
            })
            {
                MapColumns(bulk, dt);
                await bulk.WriteToServerAsync(dt, ct);
            }

            // Invoices
            var invIds = new Guid[vols.InvoicesPerSupplier];
            for (int i = 0; i < vols.InvoicesPerSupplier; i++)
                invIds[i] = DeterministicId.From("INV", $"{supplierCode}|{i:D6}");
            using (var dt = BuildInvoiceTable(supplierId, supplierCode, seccodeId, invIds, poIds))
            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, tx)
            {
                DestinationTableName = "[proc].[Invoice]",
                BatchSize = 5000,
                BulkCopyTimeout = 1200
            })
            {
                MapColumns(bulk, dt);
                await bulk.WriteToServerAsync(dt, ct);
            }

            // Invoice lines via INSERT…SELECT
            await conn.ExecuteAsync(@"
                INSERT INTO [proc].[InvoiceLine]
                  (invoiceLineId, invoiceId, purchaseOrderLineId, itemCode, itemDescription,
                   billedQty, unitPrice, lineAmount, taxAmount,
                   createdOn, createdBy, isDeleted)
                SELECT NEWID(), i.invoiceId,
                       (SELECT TOP 1 pl.purchaseOrderLineId FROM [proc].[PurchaseOrderLine] pl WHERE pl.purchaseOrderId=i.purchaseOrderId ORDER BY pl.positionNo),
                       'ITM-00001', 'Sample item',
                       50.0, 50.0, 2500.0, 0.0,
                       SYSUTCDATETIME(), 'seed', 0
                FROM [proc].[Invoice] i
                WHERE i.supplierId=@sid",
                new { sid = supplierId }, tx, commandTimeout: 1200);

            // GoodsReceipts via INSERT…SELECT against the first PO line
            await conn.ExecuteAsync(@"
                INSERT INTO [proc].[GoodsReceipt]
                  (goodsReceiptId, grnNumber, purchaseOrderLineId, receivedQty, shortQty, rejectedQty, grnDate, seccodeId,
                   createdOn, createdBy, isDeleted)
                SELECT TOP (@cnt)
                       NEWID(),
                       CONCAT('GRN-', @sc, '-', RIGHT('000000' + CAST(ROW_NUMBER() OVER (ORDER BY pl.purchaseOrderLineId) AS varchar(6)), 6)),
                       pl.purchaseOrderLineId, 100.0, 0.0, 0.0, SYSUTCDATETIME(), @sec,
                       SYSUTCDATETIME(), 'seed', 0
                FROM [proc].[PurchaseOrderLine] pl
                JOIN [proc].[PurchaseOrder] p ON p.purchaseOrderId=pl.purchaseOrderId
                WHERE p.supplierId=@sid",
                new { cnt = vols.Grns, sid = supplierId, sc = supplierCode, sec = seccodeId }, tx, commandTimeout: 1200);

            // Payments via INSERT…SELECT against invoices
            await conn.ExecuteAsync(@"
                INSERT INTO [proc].[Payment]
                  (paymentId, paymentReference, invoiceId, supplierId, paymentDate, paymentAmount,
                   tdsDeducted, netPaid, seccodeId,
                   createdOn, createdBy, isDeleted)
                SELECT TOP (@cnt)
                       NEWID(),
                       CONCAT('PAY-', @sc, '-', RIGHT('000000' + CAST(ROW_NUMBER() OVER (ORDER BY i.invoiceId) AS varchar(6)), 6)),
                       i.invoiceId, i.supplierId, SYSUTCDATETIME(), i.netAmount, 0.0, i.netAmount, @sec,
                       SYSUTCDATETIME(), 'seed', 0
                FROM [proc].[Invoice] i
                WHERE i.supplierId=@sid",
                new { cnt = vols.Payments, sid = supplierId, sc = supplierCode, sec = seccodeId }, tx, commandTimeout: 1200);

            await tx.CommitAsync(ct);
            Console.WriteLine($"[backfill] {supplierCode}: done");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ====================== DataTable builders ======================

    private static DataTable BuildPoTable(Guid supplierId, string supplierCode, Guid seccodeId, Guid[] ids)
    {
        var dt = new DataTable();
        dt.Columns.Add("purchaseOrderId", typeof(Guid));
        dt.Columns.Add("poNumber", typeof(string));
        dt.Columns.Add("supplierId", typeof(Guid));
        dt.Columns.Add("poType", typeof(string));
        dt.Columns.Add("poDate", typeof(DateTime));
        dt.Columns.Add("poStatus", typeof(string));
        dt.Columns.Add("version", typeof(int));
        dt.Columns.Add("seccodeId", typeof(Guid));
        dt.Columns.Add("createdOn", typeof(DateTime));
        dt.Columns.Add("createdBy", typeof(string));
        dt.Columns.Add("isDeleted", typeof(bool));

        var now = DateTime.UtcNow;
        var statuses = new[] { PoStatus.Released, PoStatus.Acknowledged, PoStatus.Accepted, PoStatus.PartiallyDelivered, PoStatus.Delivered, PoStatus.Closed };
        for (int i = 0; i < ids.Length; i++)
        {
            dt.Rows.Add(
                ids[i],
                $"PO-{supplierCode}-{i:D6}",
                supplierId,
                (i % 10 == 0 ? PoType.Service : PoType.Material).ToString(),
                now.AddDays(-(i % 365)),
                statuses[i % statuses.Length].ToString(),
                1,
                seccodeId,
                now,
                "seed",
                false
            );
        }
        return dt;
    }

    private static DataTable BuildAsnTable(Guid supplierId, string supplierCode, Guid seccodeId, Guid[] asnIds, Guid[] poIds)
    {
        var dt = new DataTable();
        dt.Columns.Add("asnId", typeof(Guid));
        dt.Columns.Add("asnNumber", typeof(string));
        dt.Columns.Add("purchaseOrderId", typeof(Guid));
        dt.Columns.Add("supplierId", typeof(Guid));
        dt.Columns.Add("expectedDeliveryDate", typeof(DateTime));
        dt.Columns.Add("asnStatus", typeof(string));
        dt.Columns.Add("seccodeId", typeof(Guid));
        dt.Columns.Add("createdOn", typeof(DateTime));
        dt.Columns.Add("createdBy", typeof(string));
        dt.Columns.Add("isDeleted", typeof(bool));

        var now = DateTime.UtcNow;
        var statuses = new[] { AsnStatus.Submitted, AsnStatus.InTransit, AsnStatus.Delivered };
        for (int i = 0; i < asnIds.Length; i++)
        {
            dt.Rows.Add(
                asnIds[i],
                $"ASN-{supplierCode}-{i:D6}",
                poIds[i % poIds.Length],
                supplierId,
                now.AddDays(-(i % 90)),
                statuses[i % statuses.Length].ToString(),
                seccodeId,
                now,
                "seed",
                false
            );
        }
        return dt;
    }

    private static DataTable BuildDeliveryScheduleTable(Guid seccodeId, Guid[] dsIds, Guid[] poIds)
    {
        var dt = new DataTable();
        dt.Columns.Add("deliveryScheduleId", typeof(Guid));
        dt.Columns.Add("purchaseOrderId", typeof(Guid));
        dt.Columns.Add("proposedDate", typeof(DateTime));
        dt.Columns.Add("scheduleStatus", typeof(string));
        dt.Columns.Add("seccodeId", typeof(Guid));
        dt.Columns.Add("createdOn", typeof(DateTime));
        dt.Columns.Add("createdBy", typeof(string));
        dt.Columns.Add("isDeleted", typeof(bool));

        var now = DateTime.UtcNow;
        var statuses = new[] { ScheduleStatus.Proposed, ScheduleStatus.Approved, ScheduleStatus.Rejected };
        for (int i = 0; i < dsIds.Length; i++)
        {
            dt.Rows.Add(
                dsIds[i],
                poIds[i % poIds.Length],
                now.AddDays(7 + (i % 30)),
                statuses[i % statuses.Length].ToString(),
                seccodeId,
                now,
                "seed",
                false
            );
        }
        return dt;
    }

    private static DataTable BuildInvoiceTable(Guid supplierId, string supplierCode, Guid seccodeId, Guid[] invIds, Guid[] poIds)
    {
        var dt = new DataTable();
        dt.Columns.Add("invoiceId", typeof(Guid));
        dt.Columns.Add("invoiceNumber", typeof(string));
        dt.Columns.Add("purchaseOrderId", typeof(Guid));
        dt.Columns.Add("supplierId", typeof(Guid));
        dt.Columns.Add("invoiceDate", typeof(DateTime));
        dt.Columns.Add("invoiceAmount", typeof(decimal));
        dt.Columns.Add("taxAmount", typeof(decimal));
        dt.Columns.Add("netAmount", typeof(decimal));
        dt.Columns.Add("currencyCode", typeof(string));
        dt.Columns.Add("matchingType", typeof(string));
        dt.Columns.Add("invoiceStatus", typeof(string));
        dt.Columns.Add("seccodeId", typeof(Guid));
        dt.Columns.Add("createdOn", typeof(DateTime));
        dt.Columns.Add("createdBy", typeof(string));
        dt.Columns.Add("isDeleted", typeof(bool));

        var now = DateTime.UtcNow;
        var statuses = new[] { InvoiceStatus.Submitted, InvoiceStatus.UnderReview, InvoiceStatus.Matched, InvoiceStatus.Approved, InvoiceStatus.Paid };
        for (int i = 0; i < invIds.Length; i++)
        {
            var net = 5000.0m + (i % 1000) * 10m;
            var tax = net * 0.18m;
            dt.Rows.Add(
                invIds[i],
                $"INV-{supplierCode}-{i:D6}",
                poIds[i % poIds.Length],
                supplierId,
                now.AddDays(-(i % 180)),
                net + tax,
                tax,
                net,
                "INR",
                MatchingType.ThreeWay.ToString(),
                statuses[i % statuses.Length].ToString(),
                seccodeId,
                now,
                "seed",
                false
            );
        }
        return dt;
    }

    private static void MapColumns(SqlBulkCopy bulk, DataTable dt)
    {
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
    }
}
