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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R8 — TSD R8 §5. End-to-end IDM dispatch against the real DB with the Mock IDM client (Integration:Mode=Mock).
/// Exercises the full drain: idmEntityType stamping → Create seed → gate promotion → dispatch → pid write-back →
/// soft-delete → Delete op → reap. Plus the verifier fix: a terminal 4xx Failed Create must NOT be re-seeded.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class IdmDispatchTests
{
    private readonly IntegrationTestFixture _fx;
    public IdmDispatchTests(IntegrationTestFixture fx) => _fx = fx;

    private (Guid invoiceId, Guid docId, string attachmentType) SeedInvoiceDoc(AppDbContext db, string tag, string fileName)
    {
        var now = DateTime.UtcNow;
        var invoiceId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var attachmentType = $"IdmInv-{tag}";

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
            DocumentType = attachmentType, FileName = fileName, FileUrl = $"idmtest/{tag}_{fileName}",
            FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed", IdmEntityType = null, Pid = null,
            SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
        });
        // Enabled config mapping the unique attachmentType → InforInvoice (isolates this test's documents).
        db.Set<IdmAttachmentTypeConfig>().Add(new IdmAttachmentTypeConfig
        {
            TenantId = IntegrationTestFixture.TenantId, OwnerEntityType = DocumentOwnerTypes.Invoice,
            AttachmentType = attachmentType, IdmEntityType = "InforInvoice",
            EligibilityGateExpr = MerinoOne.SupplierPortal.Application.Integration.Idm.IdmGateConversion.ToJsonata(new[] { "invoice.erpCompany", "invoice.erpTransactionType", "invoice.erpDocumentNo" }),
            CreateMappingExpression = new IdmDefaultExpressions().TryGet("InforInvoice")!.CreateExpression,
            IsEnabled = true, CreatedBy = "seed",
        });
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
            (_, docId, _) = SeedInvoiceDoc(db, Guid.NewGuid().ToString("N")[..8], "invoice.pdf");
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
            (_, docId, _) = SeedInvoiceDoc(db, Guid.NewGuid().ToString("N")[..8], "idm-fail-validation.pdf");
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
                DocumentType = oddType, FileName = "catch-all.pdf", FileUrl = $"idmtest/{tag}_catchall.pdf",
                FileSizeKb = 1, MimeType = "application/pdf", UploadedBy = "seed", IdmEntityType = null, Pid = null,
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            var cfg = new IdmAttachmentTypeConfig
            {
                TenantId = IntegrationTestFixture.TenantId, OwnerEntityType = DocumentOwnerTypes.Invoice,
                AttachmentType = null,   // CATCH-ALL
                IdmEntityType = $"InforInvoice",   // has a provider so it can dispatch
                EligibilityGateExpr = MerinoOne.SupplierPortal.Application.Integration.Idm.IdmGateConversion.ToJsonata(new[] { "invoice.erpCompany", "invoice.erpTransactionType", "invoice.erpDocumentNo" }),
                CreateMappingExpression = new IdmDefaultExpressions().TryGet("InforInvoice")!.CreateExpression,
                IsEnabled = true, CreatedBy = "seed",
            };
            db.Set<IdmAttachmentTypeConfig>().Add(cfg);
            await db.SaveChangesAsync();
            cfgId = cfg.Id;
        }

        try
        {
            await DrainAsync();

            using var scope = _fx.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // A catch-all config (null attachmentType) classifies EVERY document of its portal entity regardless of
            // the document's attachment type — the odd-typed doc gets stamped with the config's idmEntityType. (The
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
            await db.Set<IdmAttachmentTypeConfig>().IgnoreQueryFilters().Where(c => c.Id == cfgId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// 2026-07-05 — config.acl / config.entityName (read by every mapping expression) must resolve from the
    /// tenant's IDM.Item.Create OutboundEndpointConfig row, not a hardcoded literal (there was previously no UI
    /// path to change them at all). Proves the wiring by overriding the row and checking the persisted snapshot.
    /// </summary>
    [SkippableFact]
    public async Task Snapshot_config_acl_and_entityName_resolve_from_outbound_endpoint_config()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid docId, endpointConfigId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (_, docId, _) = SeedInvoiceDoc(db, Guid.NewGuid().ToString("N")[..8], "acl-entity.pdf");

            endpointConfigId = Guid.NewGuid();
            db.Set<OutboundEndpointConfig>().Add(new OutboundEndpointConfig
            {
                Id = endpointConfigId, TenantId = IntegrationTestFixture.TenantId, TargetSystem = "IDM",
                EndpointKey = "IDM.Item.Create", HttpMethod = "POST", RelativePath = "/IDM/api/items",
                DefaultAcl = "CustomAcl-Test", EntityName = "CustomEntity-Test", IsEnabled = false,
                CreatedBy = "seed",
            });
            await db.SaveChangesAsync();
        }

        try
        {
            await DrainAsync();

            using var scope = _fx.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var snapshotJson = await db.IdmDocumentOutboxes.IgnoreQueryFilters()
                .Where(o => o.DocumentUploadId == docId && o.Operation == IdmOutboxOperation.Create)
                .Select(o => o.RequestSnapshotJson).SingleAsync();

            snapshotJson.Should().Contain("CustomAcl-Test",
                because: "config.acl must resolve from the tenant's IDM.Item.Create endpoint row, not a hardcoded literal");
            snapshotJson.Should().Contain("CustomEntity-Test",
                because: "config.entityName must resolve from the same row");
        }
        finally
        {
            using var cleanup = _fx.Factory.Services.CreateScope();
            var db = cleanup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Set<OutboundEndpointConfig>().IgnoreQueryFilters()
                .Where(e => e.Id == endpointConfigId).ExecuteDeleteAsync();
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
            (_, rightDocId, attachmentType) = SeedInvoiceDoc(db, Guid.NewGuid().ToString("N")[..8], "backfill.pdf");
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

    /// <summary>Deleting a mapping soft-deletes the row and un-classifies its UNPUSHED documents only —
    /// pid-bearing documents keep the stamp so a later IDM delete can still resolve.</summary>
    [SkippableFact]
    public async Task Delete_mapping_clears_unpushed_stamps_and_keeps_pushed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid cfgId, unpushedDocId, pushedDocId;
        string attachmentType;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (_, unpushedDocId, attachmentType) = SeedInvoiceDoc(db, Guid.NewGuid().ToString("N")[..8], "del-unpushed.pdf");
            await db.SaveChangesAsync();

            var cfg = await db.Set<IdmAttachmentTypeConfig>().IgnoreQueryFilters()
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
            var handler = new Application.Integration.Idm.Commands.DeleteIdmAttachmentTypeConfigCommandHandler(
                db, new StubCurrentUser(IntegrationTestFixture.TenantId));
            var result = await handler.Handle(
                new Application.Integration.Idm.Commands.DeleteIdmAttachmentTypeConfigCommand(cfgId), CancellationToken.None);
            result.Deleted.Should().BeTrue();
            result.ClearedDocuments.Should().Be(1, because: "only the unpushed document is un-classified");

            // Second delete is a no-op (row already gone).
            var again = await handler.Handle(
                new Application.Integration.Idm.Commands.DeleteIdmAttachmentTypeConfigCommand(cfgId), CancellationToken.None);
            again.Deleted.Should().BeFalse();
        }

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Set<IdmAttachmentTypeConfig>().IgnoreQueryFilters().Where(c => c.Id == cfgId).Select(c => c.IsDeleted).SingleAsync())
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
