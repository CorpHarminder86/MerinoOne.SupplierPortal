using FluentAssertions;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// HIGH fix — money-duplication guard. Before migration 0026 the inbound payment writer dedup'd only in app memory
/// (read-modify-write in <c>UpsertPaymentsCommand</c>), so two concurrent inbound pushes for the same
/// (invoice, paymentReference) could each read "no existing row" and both insert — a duplicate payment with no DB
/// guard. 0026 adds the filtered unique index <c>UX_Payment_tenant_invoice_paymentReference</c> on
/// (tenantId, tenantEntityId, invoiceId, paymentReference) WHERE isDeleted=0 AND paymentReference IS NOT NULL.
///
/// <para>This reproduces the concurrent racer with TWO INDEPENDENT DbContexts: the first commits a payment; the
/// second (which never saw the first row in its change-tracker — exactly the race) is rejected by the DB at flush.
/// Asserts the rejection is OUR unique index by name. Cleans up in a finally so it is re-runnable.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PaymentUniqueIndexTests
{
    private readonly IntegrationTestFixture _fx;
    public PaymentUniqueIndexTests(IntegrationTestFixture fx) => _fx = fx;

    private static Payment NewPayment(Guid id, string payRef) => new()
    {
        Id = id,
        PaymentReference = payRef,
        InvoiceId = IntegrationTestFixture.InvoiceId,
        SupplierId = IntegrationTestFixture.SupplierId,
        PaymentDate = DateTime.UtcNow,
        PaymentAmount = 100m,
        TdsDeducted = 0m,
        NetPaid = 100m,
        SeccodeId = IntegrationTestFixture.SeccodeId,
        TenantId = IntegrationTestFixture.TenantId,
        TenantEntityId = IntegrationTestFixture.CompanyId,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow,
    };

    [SkippableFact]
    public async Task Concurrent_duplicate_payment_same_invoice_and_reference_is_rejected_by_the_unique_index()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var payRef = ("PAY-UQ-" + Guid.NewGuid().ToString("N"))[..20];
        try
        {
            // 1. First payment commits fine (legit inserts are NOT blocked by the index).
            using (var s1 = _fx.Factory.Services.CreateScope())
            {
                var db1 = s1.ServiceProvider.GetRequiredService<AppDbContext>();
                db1.Payments.Add(NewPayment(Guid.NewGuid(), payRef));
                await db1.SaveChangesAsync();
            }

            // 2. A second, INDEPENDENT context (the concurrent racer) inserting the same (invoice, paymentReference)
            //    is rejected by the DB unique index even though its change-tracker never saw the first row.
            using (var s2 = _fx.Factory.Services.CreateScope())
            {
                var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
                db2.Payments.Add(NewPayment(Guid.NewGuid(), payRef));

                Func<Task> act = () => db2.SaveChangesAsync();
                var ex = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
                (ex.InnerException?.Message ?? ex.Message)
                    .Should().Contain("UX_Payment_tenant_invoice_paymentReference",
                        "the duplicate must be rejected by OUR unique index, not some other constraint");
            }
        }
        finally
        {
            // Re-runnable: hard-remove the committed row(s) for this test's unique reference.
            using var sc = _fx.Factory.Services.CreateScope();
            var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.Payments.IgnoreQueryFilters().Where(p => p.PaymentReference == payRef).ToListAsync();
            if (rows.Count > 0)
            {
                db.Payments.RemoveRange(rows);
                await db.SaveChangesAsync();
            }
        }
    }
}
