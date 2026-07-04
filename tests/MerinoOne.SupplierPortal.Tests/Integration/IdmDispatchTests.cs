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
            TenantId = IntegrationTestFixture.TenantId, AttachmentType = attachmentType, IdmEntityType = "InforInvoice",
            EligibilityGateJson = "[\"invoice.erpCompany\",\"invoice.erpTransactionType\",\"invoice.erpDocumentNo\"]",
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
}
