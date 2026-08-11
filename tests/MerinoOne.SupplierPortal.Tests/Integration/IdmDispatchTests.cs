using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.SystemSettings.InforIdm;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R8 — TSD R8 §5 (R10: configs live on the unified integration.OutboundIntegrationConfig, Kind=Document).
/// End-to-end IDM dispatch against the real DB with the Mock IDM client (Integration:Mode=Mock).
/// Exercises the full drain: idmEntityType stamping → Create seed → gate promotion → dispatch → pid write-back →
/// soft-delete → Delete op → reap. Plus the verifier fix: a terminal 4xx Failed Create must NOT be re-seeded.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class IdmDispatchTests
{
    private readonly IntegrationTestFixture _fx;
    public IdmDispatchTests(IntegrationTestFixture fx) => _fx = fx;

    private static OutboundIntegrationConfig NewDocumentConfig(string? attachmentType) => new()
    {
        TenantId = IntegrationTestFixture.TenantId,
        Kind = OutboundIntegrationKind.Document,
        PortalEntity = DocumentOwnerTypes.Invoice,
        AttachmentType = attachmentType,
        TargetEntityName = "InforInvoice",
        EndpointPath = "/IDM/api/items",
        HttpVerb = "POST",
        DeleteVerb = "DELETE",
        ResponseFormat = "Xml",
        DispatchMode = OutboundDispatchMode.Dynamic,
        EligibilityGateExpr = MerinoOne.SupplierPortal.Application.Integration.Idm.IdmGateConversion.ToJsonata(
            new[] { "invoice.erpCompany", "invoice.erpTransactionType", "invoice.erpDocumentNo" }),
        RequestMappingExpr = new IdmDefaultExpressions().TryGet("InforInvoice")!.CreateExpression,
        CreatedBy = "seed",
    };

    /// <summary>Stores REAL bytes for the document (the R10 worker fails a dispatch terminally when the
    /// stored file is missing — a fake FileUrl would turn every Create into that failure).</summary>
    private async Task<string> StoreRealFileAsync(string fileName)
    {
        var storage = _fx.Factory.Services.GetRequiredService<Application.Common.Interfaces.IFileStorageService>();
        await using var ms = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });   // "%PDF"
        var stored = await storage.StoreAsync(ms, fileName, "application/pdf", Guid.NewGuid());
        return stored.StorageKey;
    }

    private async Task<(Guid invoiceId, Guid docId, string attachmentType)> SeedInvoiceDocAsync(AppDbContext db, string tag, string fileName)
    {
        var now = DateTime.UtcNow;
        var invoiceId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var attachmentType = $"IdmInv-{tag}";
        var storageKey = await StoreRealFileAsync(fileName);

        db.Invoices.Add(new Invoice
        {
            Id = invoiceId, InvoiceNumber = $"IDM-{tag}", SupplierId = IntegrationTestFixture.SupplierId,
            InvoiceDate = now.Date, InvoiceAmount = 100, TaxAmount = 0, NetAmount = 100, CurrencyCode = "INR",
            InvoiceStatus = InvoiceStatus.Submitted,
            ErpCompany = "2000", ErpTransactionType = "1DS", ErpDocumentNo = $"LN-{tag}",   // gate-satisfying
            SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
        });
        db.DocumentUploads.Add(new DocumentUpload
        {
            Id = docId, OwnerEntityType = DocumentOwnerTypes.Invoice, OwnerEntityId = invoiceId,
            DocumentType = attachmentType, FileName = fileName, FileUrl = storageKey,
            FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed", IdmEntityType = null, Pid = null,
            SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
        });
        // Active (Dynamic) Document-kind config mapping the unique attachmentType → InforInvoice (isolates this test's documents).
        db.OutboundIntegrationConfigs.Add(NewDocumentConfig(attachmentType));
        return (invoiceId, docId, attachmentType);
    }

    private async Task DrainAsync()
    {
        var sf = _fx.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var settings = _fx.Factory.Services.GetRequiredService<IInforIdmSettings>();
        await IdmDocumentOutboxWorker.DrainOnceAsync(sf, settings, NullLogger.Instance, CancellationToken.None);
    }

    [SkippableFact]
    public async Task Create_dispatch_stamps_pid_then_delete_reaps()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid docId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (_, docId, _) = await SeedInvoiceDocAsync(db, Guid.NewGuid().ToString("N")[..8], "invoice.pdf");
            await db.SaveChangesAsync();
        }

        await DrainAsync();

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                .SingleAsync(o => o.DocumentUploadId == docId && o.Operation == IdmOutboxOperation.Create);
            row.Status.Should().Be(IdmOutboxStatus.Success, because: "the Mock client acks a create with a pid");
            row.ExternalId.Should().NotBeNullOrEmpty();

            var docPid = await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.Id == docId).Select(d => d.Pid).SingleAsync();
            docPid.Should().Be(row.ExternalId, because: "a successful create stamps the pid onto the document (D-R8-24)");

            // Now soft-delete the document → the next drain should emit a Delete then reap terminal rows.
            await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.Id == docId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsDeleted, true).SetProperty(d => d.DeletedOn, DateTime.UtcNow));
        }

        await DrainAsync(); // seeds + dispatches the Delete
        await DrainAsync(); // reaps terminal rows after the delete-ack

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var deletes = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                .Where(o => o.DocumentUploadId == docId && o.Operation == IdmOutboxOperation.Delete).ToListAsync();
            deletes.Should().HaveCount(1, because: "exactly one Delete op is emitted per soft-deleted synced document");
            deletes[0].Status.Should().Be(IdmOutboxStatus.Success);
            deletes[0].IsDeleted.Should().BeTrue(because: "a successful delete reaps the outbox rows");
        }
    }

    [SkippableFact]
    public async Task Validation_4xx_failure_is_terminal_and_not_reseeded()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid docId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The Mock client returns a 400 when the payload contains this marker (via the filename).
            (_, docId, _) = await SeedInvoiceDocAsync(db, Guid.NewGuid().ToString("N")[..8], "idm-fail-validation.pdf");
            await db.SaveChangesAsync();
        }

        await DrainAsync(); // Create → 400 → Failed
        await DrainAsync(); // must NOT create a second Create row

        using var s = _fx.Factory.Services.CreateScope();
        var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var creates = await d.IdmDocumentOutboxes.IgnoreQueryFilters()
            .Where(o => o.DocumentUploadId == docId && o.Operation == IdmOutboxOperation.Create).ToListAsync();

        creates.Should().HaveCount(1, because: "a terminal 4xx Failed create must not be re-seeded every drain (D-R8-23)");
        creates[0].Status.Should().Be(IdmOutboxStatus.Failed);
        creates[0].LastError.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// 2026-08-11 STARVATION REGRESSION — the seed scan must not spend its batch budget on documents that ALREADY
    /// carry a Create row. Pre-fix the candidate query was an unordered <c>Take(batchSize)</c> over every unsynced
    /// document with the dedupe applied in memory afterwards, so once the first batchSize candidates all had rows
    /// every subsequent drain re-picked exactly those and seeded nothing — newer uploads were never reached (dev:
    /// 31 ASN candidates at batchSize 25 → the 6 newest never seeded, so a document whose ASN had since gained an
    /// erpCode could not even be gate-evaluated). Three documents, batchSize 2: the third can only appear if the
    /// second pass skips the two already-seeded ones. The gate is deliberately UNSATISFIED (the invoice carries no
    /// erp trio), so the rows stay Blocked and no dispatch is involved — this is a seeding test.
    /// </summary>
    [SkippableFact]
    public async Task Seed_scan_reaches_newer_documents_when_the_batch_is_full_of_already_seeded_ones()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var attachmentType = $"IdmStarve-{tag}";
        var docIds = new List<Guid>();
        Guid cfgId;

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var invoiceId = Guid.NewGuid();
            db.Invoices.Add(new Invoice
            {
                Id = invoiceId, InvoiceNumber = $"STARVE-{tag}", SupplierId = IntegrationTestFixture.SupplierId,
                InvoiceDate = now.Date, InvoiceAmount = 100, TaxAmount = 0, NetAmount = 100, CurrencyCode = "INR",
                InvoiceStatus = InvoiceStatus.Submitted,
                ErpCompany = null, ErpTransactionType = null, ErpDocumentNo = null,   // gate stays UNSATISFIED
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
            });

            for (var i = 1; i <= 3; i++)
            {
                var docId = Guid.NewGuid();
                docIds.Add(docId);
                db.DocumentUploads.Add(new DocumentUpload
                {
                    Id = docId, OwnerEntityType = DocumentOwnerTypes.Invoice, OwnerEntityId = invoiceId,
                    DocumentType = attachmentType, FileName = $"starve-{tag}-{i}.pdf",
                    FileUrl = $"idmtest/starve-{tag}-{i}.pdf",   // never dispatched (Blocked) — no real bytes needed
                    FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed", IdmEntityType = null, Pid = null,
                    SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                    TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now.AddSeconds(i),
                });
            }

            var cfg = NewDocumentConfig(attachmentType);
            db.OutboundIntegrationConfigs.Add(cfg);
            await db.SaveChangesAsync();
            cfgId = cfg.Id;
        }

        try
        {
            // batchSize 2 < 3 candidates: pass 1 can seed at most two, pass 2 must reach the remaining one.
            await SeedPassAsync(batchSize: 2);
            await SeedPassAsync(batchSize: 2);

            using var scope = _fx.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                .Where(o => !o.IsDeleted && o.Operation == IdmOutboxOperation.Create && docIds.Contains(o.DocumentUploadId))
                .ToListAsync();

            rows.Select(r => r.DocumentUploadId).Should().BeEquivalentTo(docIds,
                because: "a batch already full of seeded documents must not starve the ones still needing a row");
            rows.Should().OnlyContain(r => r.Status == IdmOutboxStatus.Blocked,
                because: "the invoice carries no erp trio, so the eligibility gate withholds every row");
        }
        finally
        {
            using var cleanup = _fx.Factory.Services.CreateScope();
            var db = cleanup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OutboundIntegrationConfigs.IgnoreQueryFilters().Where(c => c.Id == cfgId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// 2026-08-11 STARVATION REGRESSION (dispatch side) — head selection must not spend its batch budget on
    /// partitions that yield no dispatchable head. Pre-fix it took an unordered <c>Distinct().Take(batchSize)</c>
    /// over the partitions holding a due Pending row and only afterwards dropped the ones whose head was
    /// InFlight/Blocked, so a queue of blocked-head partitions permanently hid every later partition. Two
    /// blocked-head partitions (each with a Pending successor) plus one plain Pending partition, with a budget of
    /// exactly one slot beyond the pre-existing heads: the slot must reach the dispatchable partition, and the
    /// held successors must never be selected.
    /// </summary>
    [SkippableFact]
    public async Task Head_selection_skips_blocked_head_partitions_without_spending_the_batch_budget()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var docIds = new List<Guid>();
        var heldSuccessorIds = new List<Guid>();
        Guid dispatchableRowId;
        var now = DateTime.UtcNow;

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            IdmDocumentOutbox NewRow(Guid docId, IdmOutboxOperation op, IdmOutboxStatus status) => new()
            {
                Id = Guid.NewGuid(), DocumentUploadId = docId, IdmEntityType = "InforInvoice",
                OwnerEntityId = Guid.NewGuid(), FileName = $"head-{tag}.pdf",
                Operation = op, Status = status,
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed",
            };

            // A DocumentUpload per partition (FK target). Their DocumentType matches no config, so no scan touches them.
            Guid NewDoc(string suffix)
            {
                var docId = Guid.NewGuid();
                db.DocumentUploads.Add(new DocumentUpload
                {
                    Id = docId, OwnerEntityType = DocumentOwnerTypes.Invoice, OwnerEntityId = Guid.NewGuid(),
                    DocumentType = $"IdmHead-{tag}-{suffix}", FileName = $"head-{tag}-{suffix}.pdf",
                    FileUrl = $"idmtest/head-{tag}-{suffix}.pdf", FileSizeKb = 1, MimeType = "application/pdf",
                    UploadedBy = "seed", IdmEntityType = null, Pid = null,
                    SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                    TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
                });
                docIds.Add(docId);
                return docId;
            }

            // Two partitions whose HEAD is a Blocked Create with a due Pending DELETE queued behind it — the real
            // shape of "the document was deleted while its create was still gated". Seq is assigned in insert
            // order, so the head must be saved first. (A second Create would be the shape migration 0061 forbids;
            // a partition stacks DIFFERENT operations, which is exactly why that index is per-operation.)
            foreach (var suffix in new[] { "blocked1", "blocked2" })
            {
                var docId = NewDoc(suffix);
                db.IdmDocumentOutboxes.Add(NewRow(docId, IdmOutboxOperation.Create, IdmOutboxStatus.Blocked));
                await db.SaveChangesAsync();
                var successor = NewRow(docId, IdmOutboxOperation.Delete, IdmOutboxStatus.Pending);
                db.IdmDocumentOutboxes.Add(successor);
                await db.SaveChangesAsync();
                heldSuccessorIds.Add(successor.Id);
            }

            // One plain due-Pending partition — the only dispatchable head of the three, and the newest of all.
            var dispatchable = NewRow(NewDoc("pending"), IdmOutboxOperation.Create, IdmOutboxStatus.Pending);
            db.IdmDocumentOutboxes.Add(dispatchable);
            await db.SaveChangesAsync();
            dispatchableRowId = dispatchable.Id;

            // The shared test DB has the real dispatcher polling it (the fixture leaves the hosted workers
            // running), so the global set of due heads shifts under the test. Assert the two invariants against an
            // unbounded read, then re-derive the tight budget from that same read and retry a couple of times if a
            // foreign head appears in between — the flake would otherwise be noise, not a regression.
            List<Guid> heads = null!;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                heads = await IdmDocumentOutboxWorker.SelectDueHeadRowsAsync(db, 10_000, DateTime.UtcNow, CancellationToken.None);

                heads.Should().Contain(dispatchableRowId,
                    because: "a plain due-Pending partition is a dispatchable head");
                heads.Should().NotContain(heldSuccessorIds,
                    because: "a successor stays held behind its non-terminal head (per-partition FIFO)");

                // Exactly enough budget to reach our row: it can only survive the Take if the two blocked-head
                // partitions never occupied a slot, i.e. they are excluded IN the query, not after it.
                var tight = await IdmDocumentOutboxWorker.SelectDueHeadRowsAsync(
                    db, heads.IndexOf(dispatchableRowId) + 1, DateTime.UtcNow, CancellationToken.None);
                if (tight.Contains(dispatchableRowId) || attempt == 3)
                {
                    tight.Should().Contain(dispatchableRowId,
                        because: "partitions whose head is Blocked must not consume the dispatch batch budget");
                    break;
                }
            }
        }

        using (var cleanup = _fx.Factory.Services.CreateScope())
        {
            var db = cleanup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.IdmDocumentOutboxes.IgnoreQueryFilters().Where(o => docIds.Contains(o.DocumentUploadId)).ExecuteDeleteAsync();
            await db.DocumentUploads.IgnoreQueryFilters().Where(d => docIds.Contains(d.Id)).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// Migration 0061 — <c>UQ_IdmDocumentOutbox_documentUploadId_operation_live</c>. Two dispatcher instances
    /// racing one poll used to seed the same document twice and push two items to IDM (observed on the test DB:
    /// two Create rows 76 ms apart, both dispatched). The index makes the seed scan's in-SQL dedupe durable.
    /// Covers the three semantics the filter has to preserve at once: a live duplicate is rejected, a TERMINAL
    /// Failed Create still blocks a re-seed (D-R8-23), and a REAPED row releases the slot so the
    /// create→delete→reap→re-seed cycle stays open. Update rows are outside the filter and stay unconstrained.
    /// </summary>
    [SkippableFact]
    public async Task Unique_live_index_allows_one_create_per_document_and_releases_on_reap()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        Guid docId;

        IdmDocumentOutbox NewRow(IdmOutboxOperation op, IdmOutboxStatus status, Guid document) => new()
        {
            Id = Guid.NewGuid(), DocumentUploadId = document, IdmEntityType = "InforInvoice",
            OwnerEntityId = Guid.NewGuid(), FileName = $"uq-{tag}.pdf", Operation = op, Status = status,
            SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed",
        };

        try
        {
            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                docId = Guid.NewGuid();
                db.DocumentUploads.Add(new DocumentUpload
                {
                    Id = docId, OwnerEntityType = DocumentOwnerTypes.Invoice, OwnerEntityId = Guid.NewGuid(),
                    DocumentType = $"IdmUq-{tag}", FileName = $"uq-{tag}.pdf", FileUrl = $"idmtest/uq-{tag}.pdf",
                    FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed", IdmEntityType = null, Pid = null,
                    SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                    TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
                });
                // A TERMINAL Failed Create — still live (not reaped), so it must hold the slot.
                db.IdmDocumentOutboxes.Add(NewRow(IdmOutboxOperation.Create, IdmOutboxStatus.Failed, docId));
                await db.SaveChangesAsync();
            }

            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.IdmDocumentOutboxes.Add(NewRow(IdmOutboxOperation.Create, IdmOutboxStatus.Pending, docId));
                var act = async () => await db.SaveChangesAsync();
                (await act.Should().ThrowAsync<DbUpdateException>(
                        because: "a second live Create for the same document is exactly the duplicate-push race"))
                    .Which.InnerException.Should().BeOfType<SqlException>()
                    .Which.Number.Should().BeOneOf(2601, 2627);
            }

            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // An Update row is outside the filter — two live ones must still be accepted.
                db.IdmDocumentOutboxes.Add(NewRow(IdmOutboxOperation.Update, IdmOutboxStatus.Pending, docId));
                db.IdmDocumentOutboxes.Add(NewRow(IdmOutboxOperation.Update, IdmOutboxStatus.Success, docId));
                await db.SaveChangesAsync();

                // Reap the Create row → the slot is released and the document can be seeded again.
                await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                    .Where(o => o.DocumentUploadId == docId && o.Operation == IdmOutboxOperation.Create)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true).SetProperty(o => o.DeletedOn, DateTime.UtcNow));

                db.IdmDocumentOutboxes.Add(NewRow(IdmOutboxOperation.Create, IdmOutboxStatus.Blocked, docId));
                var act = async () => await db.SaveChangesAsync();
                await act.Should().NotThrowAsync(
                    because: "a reaped Create leaves the filter, so create→delete→reap→re-seed stays possible");
            }
        }
        finally
        {
            using var cleanup = _fx.Factory.Services.CreateScope();
            var db = cleanup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.IdmDocumentOutboxes.IgnoreQueryFilters().Where(o => o.FileName == $"uq-{tag}.pdf").ExecuteDeleteAsync();
            await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.DocumentType == $"IdmUq-{tag}").ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// Migration 0061 companion — the seed scan must SURVIVE losing the race, not blow up the drain. A concurrent
    /// dispatcher's Create row is planted on a separate connection between the scan's read and its write; the pass
    /// must swallow the unique violation and the next pass must still seed everything else.
    /// </summary>
    [SkippableFact]
    public async Task Seed_scan_swallows_a_lost_race_and_still_seeds_the_rest_next_pass()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var attachmentType = $"IdmRace-{tag}";
        Guid racedDocId, otherDocId, cfgId;

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var invoiceId = Guid.NewGuid();
            db.Invoices.Add(new Invoice
            {
                Id = invoiceId, InvoiceNumber = $"RACE-{tag}", SupplierId = IntegrationTestFixture.SupplierId,
                InvoiceDate = now.Date, InvoiceAmount = 100, TaxAmount = 0, NetAmount = 100, CurrencyCode = "INR",
                InvoiceStatus = InvoiceStatus.Submitted,
                ErpCompany = null, ErpTransactionType = null, ErpDocumentNo = null,   // gate stays UNSATISFIED
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
            });

            Guid NewDoc(string suffix)
            {
                var docId = Guid.NewGuid();
                db.DocumentUploads.Add(new DocumentUpload
                {
                    Id = docId, OwnerEntityType = DocumentOwnerTypes.Invoice, OwnerEntityId = invoiceId,
                    DocumentType = attachmentType, FileName = $"race-{tag}-{suffix}.pdf",
                    FileUrl = $"idmtest/race-{tag}-{suffix}.pdf", FileSizeKb = 1, MimeType = "application/pdf",
                    UploadedBy = "seed", IdmEntityType = null, Pid = null,
                    SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                    TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
                });
                return docId;
            }

            racedDocId = NewDoc("raced");
            otherDocId = NewDoc("other");
            var cfg = NewDocumentConfig(attachmentType);
            db.OutboundIntegrationConfigs.Add(cfg);
            await db.SaveChangesAsync();
            cfgId = cfg.Id;
        }

        try
        {
            // The "other dispatcher": plant a Create row for one of the two documents before the scan writes.
            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.IdmDocumentOutboxes.Add(new IdmDocumentOutbox
                {
                    Id = Guid.NewGuid(), DocumentUploadId = racedDocId, IdmEntityType = "InforInvoice",
                    OwnerEntityId = Guid.NewGuid(), FileName = $"race-{tag}-raced.pdf",
                    Operation = IdmOutboxOperation.Create, Status = IdmOutboxStatus.Blocked,
                    SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                    TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "other-dispatcher",
                });
                await db.SaveChangesAsync();
            }

            // This pass may or may not collide depending on read timing; either way it must not throw…
            var pass = async () => await SeedPassAsync(batchSize: 25);
            await pass.Should().NotThrowAsync(because: "a lost seed race is a log line, not a failed drain");

            // …and a follow-up pass must leave both documents with exactly one live Create row.
            await SeedPassAsync(batchSize: 25);

            using var check = _fx.Factory.Services.CreateScope();
            var verify = check.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var docId in new[] { racedDocId, otherDocId })
                (await verify.IdmDocumentOutboxes.IgnoreQueryFilters()
                    .CountAsync(o => !o.IsDeleted && o.Operation == IdmOutboxOperation.Create && o.DocumentUploadId == docId))
                    .Should().Be(1, because: "every candidate ends with exactly one live Create row");
        }
        finally
        {
            using var cleanup = _fx.Factory.Services.CreateScope();
            var db = cleanup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OutboundIntegrationConfigs.IgnoreQueryFilters().Where(c => c.Id == cfgId).ExecuteDeleteAsync();
        }
    }

    /// <summary>One maintenance pass (stamp → seed → promote) at an explicit batch size — no dispatch, no reap.</summary>
    private async Task SeedPassAsync(int batchSize)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<Application.Integration.Idm.ISnapshotProviderRegistry>();
        var gate = scope.ServiceProvider.GetRequiredService<Application.Integration.Idm.IEligibilityGate>();
        await IdmDocumentOutboxWorker.SeedAndPromoteAsync(db, registry, gate, batchSize, NullLogger.Instance, CancellationToken.None);
    }

    /// <summary>
    /// 2026-07-06 — a CATCH-ALL config (null attachmentType) classifies + seeds EVERY document of its portal
    /// entity, regardless of the document's attachment type. Proves the optional-attachment-type feature.
    /// </summary>
    [SkippableFact]
    public async Task CatchAll_config_stamps_and_seeds_every_document_of_the_entity()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        Guid invoiceId, docId, cfgId;
        var oddType = $"OddType-{tag}";   // a document type NOT matched by any specific-type config
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            invoiceId = Guid.NewGuid();
            docId = Guid.NewGuid();
            db.Invoices.Add(new Invoice
            {
                Id = invoiceId, InvoiceNumber = $"CATCH-{tag}", SupplierId = IntegrationTestFixture.SupplierId,
                InvoiceDate = DateTime.UtcNow.Date, InvoiceAmount = 100, TaxAmount = 0, NetAmount = 100, CurrencyCode = "INR",
                InvoiceStatus = InvoiceStatus.Submitted, ErpCompany = "2000", ErpTransactionType = "1DS", ErpDocumentNo = $"LN-{tag}",
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            db.DocumentUploads.Add(new DocumentUpload
            {
                Id = docId, OwnerEntityType = DocumentOwnerTypes.Invoice, OwnerEntityId = invoiceId,
                DocumentType = oddType, FileName = "catch-all.pdf", FileUrl = await StoreRealFileAsync("catch-all.pdf"),
                FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed", IdmEntityType = null, Pid = null,
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            var cfg = NewDocumentConfig(attachmentType: null);   // CATCH-ALL
            db.OutboundIntegrationConfigs.Add(cfg);
            await db.SaveChangesAsync();
            cfgId = cfg.Id;
        }

        try
        {
            await DrainAsync();

            using var scope = _fx.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // A catch-all config (null attachmentType) classifies EVERY document of its portal entity regardless of
            // the document's attachment type — the odd-typed doc gets stamped with the config's target entity. (The
            // downstream Create/dispatch mechanics are covered by the specific-type tests; here the stamp is the
            // catch-all proof, and it isn't subject to the seed-scan's per-drain Take() batch.)
            (await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.Id == docId).Select(d => d.IdmEntityType).SingleAsync())
                .Should().Be("InforInvoice",
                    because: "a catch-all config classifies every document of its portal entity, whatever the attachment type");
        }
        finally
        {
            using var cleanup = _fx.Factory.Services.CreateScope();
            var db = cleanup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.OutboundIntegrationConfigs.IgnoreQueryFilters().Where(c => c.Id == cfgId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// Option A (2026-07-08) — acl / entityName are INLINE LITERALS in the default IDM mapping ("Public" /
    /// "MDS_GenericDocument"); the <c>config.*</c> context bag and its <c>contextJson</c> source are gone. Proves
    /// the rendered request carries the literals end-to-end (regression guard for the inlined defaults).
    /// </summary>
    [SkippableFact]
    public async Task Rendered_request_carries_literal_acl_and_entity_name()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid docId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (_, docId, _) = await SeedInvoiceDocAsync(db, Guid.NewGuid().ToString("N")[..8], "acl-entity.pdf");
            await db.SaveChangesAsync();
        }

        await DrainAsync();

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshotJson = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                .Where(o => o.DocumentUploadId == docId && o.Operation == IdmOutboxOperation.Create)
                .Select(o => o.RequestSnapshotJson).SingleAsync();

            snapshotJson.Should().Contain("MDS_GenericDocument",
                because: "entityName is the inlined literal in the default mapping (was config.entityName)");
            snapshotJson.Should().Contain("Public",
                because: "acl.name is the inlined literal \"Public\" in the default mapping (was config.acl)");
        }
    }

    /// <summary>2026-07-05 fix — the manual Backfill must mirror the worker's PORTAL-ENTITY-aware predicate: a
    /// shared attachment-type code on the WRONG owner entity (e.g. supplier-owned) must not be stamped.</summary>
    [SkippableFact]
    public async Task Backfill_stamps_only_matching_portal_entity()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid rightDocId, wrongDocId;
        string attachmentType;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (_, rightDocId, attachmentType) = await SeedInvoiceDocAsync(db, Guid.NewGuid().ToString("N")[..8], "backfill.pdf");
            // Same attachment-type code, but SUPPLIER-owned — must be skipped by the entity-aware backfill.
            wrongDocId = Guid.NewGuid();
            db.DocumentUploads.Add(new DocumentUpload
            {
                Id = wrongDocId, OwnerEntityType = DocumentOwnerTypes.Supplier, OwnerEntityId = IntegrationTestFixture.SupplierId,
                DocumentType = attachmentType, FileName = "wrong-owner.pdf", FileUrl = "idmtest/wrong-owner.pdf",
                FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed", IdmEntityType = null, Pid = null,
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var registry = scope.ServiceProvider.GetRequiredService<Application.Integration.Idm.ISnapshotProviderRegistry>();
            var handler = new Application.Integration.Idm.Commands.BackfillIdmEntityTypeCommandHandler(
                db, new StubCurrentUser(IntegrationTestFixture.TenantId), registry);
            await handler.Handle(new Application.Integration.Idm.Commands.BackfillIdmEntityTypeCommand(), CancellationToken.None);
        }

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.Id == rightDocId).Select(d => d.IdmEntityType).SingleAsync())
                .Should().Be("InforInvoice", because: "the invoice-owned document matches the mapping's portal entity");
            (await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.Id == wrongDocId).Select(d => d.IdmEntityType).SingleAsync())
                .Should().BeNull(because: "a supplier-owned document must not be stamped with an invoice/ASN entity type");
        }
    }

    /// <summary>Deleting a Document integration soft-deletes the row and un-classifies its UNPUSHED documents only —
    /// pid-bearing documents keep the stamp so a later IDM delete can still resolve (R10: unified delete handler).</summary>
    [SkippableFact]
    public async Task Delete_mapping_clears_unpushed_stamps_and_keeps_pushed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid cfgId, unpushedDocId, pushedDocId;
        string attachmentType;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (_, unpushedDocId, attachmentType) = await SeedInvoiceDocAsync(db, Guid.NewGuid().ToString("N")[..8], "del-unpushed.pdf");
            await db.SaveChangesAsync();

            var cfg = await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
                .SingleAsync(c => c.TenantId == IntegrationTestFixture.TenantId && c.AttachmentType == attachmentType);
            cfgId = cfg.Id;

            // Stamp both docs as the mapping would; the second one is already pushed (pid present).
            pushedDocId = Guid.NewGuid();
            db.DocumentUploads.Add(new DocumentUpload
            {
                Id = pushedDocId, OwnerEntityType = DocumentOwnerTypes.Invoice, OwnerEntityId = Guid.NewGuid(),
                DocumentType = attachmentType, FileName = "del-pushed.pdf", FileUrl = "idmtest/del-pushed.pdf",
                FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed",
                IdmEntityType = "InforInvoice", Pid = "MDS-test-LATEST",
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.Id == unpushedDocId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IdmEntityType, "InforInvoice"));
            await db.SaveChangesAsync();
        }

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var handler = new Application.Integration.Ln.Commands.DeleteOutboundIntegrationConfigCommandHandler(
                db, new StubCurrentUser(IntegrationTestFixture.TenantId));
            var result = await handler.Handle(
                new Application.Integration.Ln.Commands.DeleteOutboundIntegrationConfigCommand(cfgId), CancellationToken.None);
            result.Should().BeTrue();

            // Second delete is a no-op (row already gone).
            var again = await handler.Handle(
                new Application.Integration.Ln.Commands.DeleteOutboundIntegrationConfigCommand(cfgId), CancellationToken.None);
            again.Should().BeFalse();
        }

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.OutboundIntegrationConfigs.IgnoreQueryFilters().Where(c => c.Id == cfgId).Select(c => c.IsDeleted).SingleAsync())
                .Should().BeTrue(because: "delete is a soft-delete");
            (await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.Id == unpushedDocId).Select(d => d.IdmEntityType).SingleAsync())
                .Should().BeNull(because: "the unpushed document loses its classification with the mapping");
            (await db.DocumentUploads.IgnoreQueryFilters().Where(d => d.Id == pushedDocId).Select(d => d.IdmEntityType).SingleAsync())
                .Should().Be("InforInvoice", because: "a pushed document keeps the stamp to resolve a later IDM delete");
        }
    }

    private sealed class StubCurrentUser(Guid tenantId) : Application.Common.Interfaces.ICurrentUser
    {
        public string UserCode => "test:idm";
        public string? UserName => "test:idm";
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public IReadOnlyCollection<string> Permissions => Array.Empty<string>();
        public bool IsAuthenticated => true;
        public bool IsManager => false;
        public bool IsAdmin => false;
        public bool HasPermission(string code) => false;
        public Guid? TenantId { get; } = tenantId;
        public bool IsPlatformAdmin => false;
    }
}
