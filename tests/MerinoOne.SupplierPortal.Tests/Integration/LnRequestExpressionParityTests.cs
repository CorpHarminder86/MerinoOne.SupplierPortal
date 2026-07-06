using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R9 — THE byte-parity regression harness (BE-A acceptance): for every transaction type, the repo
/// request expression evaluated over the input document must reproduce the legacy payload builder's
/// output EXACTLY in canonical form (<c>LnJson.CanonicalWrite</c> — the same bytes the dynamic
/// transport POSTs). Canonicalisation neutralises STJ-vs-JSONata escaping and decimal trailing-zero
/// scale; everything else must match byte-for-byte or the cutover is not safe.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LnRequestExpressionParityTests
{
    private readonly IntegrationTestFixture _fx;
    private readonly LnDefaultExpressions _catalog = new();
    private readonly LnMappingService _svc = new();

    public LnRequestExpressionParityTests(IntegrationTestFixture fx) => _fx = fx;

    private async Task AssertParityAsync(
        string transactionType,
        ILnInputDocumentBuilder builder,
        Guid entityId,
        Func<AppDbContext, Task<string?>> legacy,
        string? outboxPayloadJson = null)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var legacyJson = await legacy(db);
        legacyJson.Should().NotBeNull($"{transactionType}: legacy builder must resolve the entity");

        var inputJson = await builder.BuildJsonAsync(db, entityId, transactionType, outboxPayloadJson);
        inputJson.Should().NotBeNull($"{transactionType}: input-document builder must resolve the entity");

        var expr = _catalog.TryGet(transactionType)!.RequestExpr;
        var mapped = _svc.Evaluate(expr, inputJson!);
        mapped.Ok.Should().BeTrue($"{transactionType}: request expression evaluation failed: {mapped.Error}");
        mapped.OutputJson.Should().NotBeNull($"{transactionType}: request expression produced no output");

        var canonicalLegacy = LnJson.CanonicalWrite(legacyJson!);
        var canonicalDynamic = LnJson.CanonicalWrite(mapped.OutputJson!);
        canonicalDynamic.Should().Be(canonicalLegacy,
            $"{transactionType}: dynamic JSONata output must be byte-identical to the legacy builder in canonical form");
    }

    [SkippableFact]
    public async Task InvoicePost_parity()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await AssertParityAsync(
            OutboxTransactionType.InvoicePost,
            new InvoiceInputDocumentBuilder(),
            IntegrationTestFixture.InvoiceId,
            db => InvoiceOutboundPayloadBuilder.BuildJsonAsync(db, IntegrationTestFixture.InvoiceId));
    }

    [SkippableFact]
    public async Task AsnPost_parity_with_serials_lots_and_null_optionals()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid asnId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var tag = Guid.NewGuid().ToString("N")[..8];
            var asn = new Asn
            {
                Id = Guid.NewGuid(), AsnNumber = $"ASN-PAR-{tag}", PurchaseOrderId = IntegrationTestFixture.PoId,
                SupplierId = IntegrationTestFixture.SupplierId, ExpectedDeliveryDate = now.Date.AddDays(2),
                AsnStatus = AsnStatus.Submitted, CarrierName = "BlueDart", TrackingNumber = null, VehicleNumber = "MH12AB1234",
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
            };
            db.Asns.Add(asn);
            var line1 = new AsnLine
            {
                Id = Guid.NewGuid(), AsnId = asn.Id, PurchaseOrderLineId = IntegrationTestFixture.PoLine1Id,
                PositionNo = 10, SequenceNo = 1, ShippedQty = 40.50m, BatchNumber = "B-01",
                ExpiryDate = now.Date.AddYears(1), CreatedBy = "seed", CreatedOn = now,
            };
            line1.Serials.Add(new AsnLineSerial { Id = Guid.NewGuid(), AsnLineId = line1.Id, SerialNumber = "SER-001", CreatedBy = "seed", CreatedOn = now });
            line1.Serials.Add(new AsnLineSerial { Id = Guid.NewGuid(), AsnLineId = line1.Id, SerialNumber = "SER-002", CreatedBy = "seed", CreatedOn = now });
            var line2 = new AsnLine
            {
                Id = Guid.NewGuid(), AsnId = asn.Id, PurchaseOrderLineId = IntegrationTestFixture.PoLine2Id,
                PositionNo = 20, SequenceNo = null, ShippedQty = 5m, BatchNumber = null, ExpiryDate = null,
                CreatedBy = "seed", CreatedOn = now,
            };
            line2.Lots.Add(new AsnLineLot { Id = Guid.NewGuid(), AsnLineId = line2.Id, LotNo = "LOT-9", Qty = 5m, ExpiryDate = null, CreatedBy = "seed", CreatedOn = now });
            db.AsnLines.AddRange(line1, line2);
            await db.SaveChangesAsync();
            asnId = asn.Id;
        }

        await AssertParityAsync(
            OutboxTransactionType.AsnPost,
            new AsnInputDocumentBuilder(),
            asnId,
            db => AsnOutboundPayloadBuilder.BuildJsonAsync(db, asnId));
    }

    private async Task<Guid> SeedResponsePoAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid(), PoNumber = $"PO-PAR-{tag}", SupplierId = IntegrationTestFixture.SupplierId,
            PoType = PoType.Material, PoDate = now.Date, PoStatus = PoStatus.Acknowledged,
            AcknowledgmentAt = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc),
            AcceptedAt = new DateTime(2026, 7, 2, 9, 30, 0, DateTimeKind.Utc),
            SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
        };
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();
        return po.Id;
    }

    [SkippableFact]
    public async Task PoAcknowledge_parity()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var poId = await SeedResponsePoAsync();
        await AssertParityAsync(
            OutboxTransactionType.PoAcknowledge,
            new PurchaseOrderInputDocumentBuilder(),
            poId,
            async db =>
            {
                var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == poId);
                return PoResponseOutboundPayloadBuilder.BuildAcknowledgeJson(po);
            });
    }

    [SkippableFact]
    public async Task PoAccept_parity_with_proposed_date()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var poId = await SeedResponsePoAsync();
        var payloadJson = "{\"proposedDate\":\"2026-07-15T10:30:00Z\"}";
        await AssertParityAsync(
            OutboxTransactionType.PoAccept,
            new PurchaseOrderInputDocumentBuilder(),
            poId,
            async db =>
            {
                var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == poId);
                // EXACTLY the legacy dispatcher's lenient parse (plain TryParse — kind/zone semantics included),
                // so the legacy argument here equals what the input-document builder derives from PayloadJson.
                DateTime.TryParse("2026-07-15T10:30:00Z", out var proposed).Should().BeTrue();
                return PoResponseOutboundPayloadBuilder.BuildAcceptJson(po, proposed);
            },
            payloadJson);
    }

    [SkippableFact]
    public async Task PoReject_parity()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var poId = await SeedResponsePoAsync();
        await AssertParityAsync(
            OutboxTransactionType.PoReject,
            new PurchaseOrderInputDocumentBuilder(),
            poId,
            async db =>
            {
                var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == poId);
                return PoResponseOutboundPayloadBuilder.BuildRejectJson(po, "Price too high");
            },
            "{\"reason\":\"Price too high\"}");
    }

    private async Task<Guid> SeedSupplierWithChildrenAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = new SupplierEntity
        {
            Id = Guid.NewGuid(), SupplierCode = $"SUP-PAR-{tag}", LegalName = $"Parity Supplier {tag} Ltd",
            TradeName = null, GstNumber = "27AAAAA0000A1Z5", PanNumber = null,
            SupplierType = SupplierType.Material, RegistrationStatus = RegistrationStatus.Approved,
            IsActiveSupplier = true, PaymentTermCode = "NET30", DeliveryTermCode = null,
            SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
        };
        supplier.Addresses.Add(new SupplierAddress
        {
            Id = Guid.NewGuid(), SupplierId = supplier.Id, AddressType = "Registered",
            AddressLine1 = "1 Parity Street", AddressLine2 = null, Area = null, City = "Pune",
            State = "MH", Pincode = "411001", Country = "India", ErpCode = "AD-01",
            CreatedBy = "seed", CreatedOn = now,
        });
        supplier.Contacts.Add(new SupplierContact
        {
            Id = Guid.NewGuid(), SupplierId = supplier.Id, ContactName = "Asha K", Designation = null,
            Email = "asha@parity.example", Phone = null, IsPrimary = true, AddressId = null, ErpCode = null,
            CreatedBy = "seed", CreatedOn = now,
        });
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    [SkippableFact]
    public async Task SupplierSync_parity_with_children()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var supplierId = await SeedSupplierWithChildrenAsync();
        await AssertParityAsync(
            OutboxTransactionType.SupplierSync,
            new SupplierInputDocumentBuilder(),
            supplierId,
            db => SupplierOutboundPayloadBuilder.BuildJsonAsync(db, supplierId));
    }

    [SkippableFact]
    public async Task SupplierChange_parity_across_entity_types()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var supplierId = await SeedSupplierWithChildrenAsync();

        Guid crId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var addressId = (await db.Set<SupplierAddress>().IgnoreQueryFilters()
                .FirstAsync(a => a.SupplierId == supplierId)).Id;
            var cr = new SupplierChangeRequest
            {
                Id = Guid.NewGuid(), SupplierId = supplierId, ChangeStatus = ChangeRequestStatus.Approved,
                RequestedBy = "seed", RequestedAt = now, Summary = "Parity change",
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
            };
            cr.Lines.Add(new SupplierChangeRequestLine
            {
                Id = Guid.NewGuid(), SupplierChangeRequestId = cr.Id, TargetEntity = ChangeTargetEntity.Supplier,
                TargetEntityId = null, Operation = ChangeOperation.Edit, CreatedBy = "seed", CreatedOn = now,
            });
            cr.Lines.Add(new SupplierChangeRequestLine
            {
                Id = Guid.NewGuid(), SupplierChangeRequestId = cr.Id, TargetEntity = ChangeTargetEntity.Address,
                TargetEntityId = addressId, Operation = ChangeOperation.Edit, CreatedBy = "seed", CreatedOn = now,
            });
            cr.Lines.Add(new SupplierChangeRequestLine
            {
                // Missing target → the legacy builder emits nulls + Deleted=true; exercises the null-drop path.
                Id = Guid.NewGuid(), SupplierChangeRequestId = cr.Id, TargetEntity = ChangeTargetEntity.Contact,
                TargetEntityId = Guid.NewGuid(), Operation = ChangeOperation.Delete, CreatedBy = "seed", CreatedOn = now,
            });
            db.SupplierChangeRequests.Add(cr);
            await db.SaveChangesAsync();
            crId = cr.Id;
        }

        await AssertParityAsync(
            OutboxTransactionType.SupplierChange,
            new SupplierChangeInputDocumentBuilder(),
            crId,
            db => SupplierChangeOutboundPayloadBuilder.BuildJsonAsync(db, crId));
    }

    [SkippableFact]
    public async Task PoNegotiationApprove_parity()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        Guid negotiationId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var tag = Guid.NewGuid().ToString("N")[..8];
            var neg = new PurchaseOrderNegotiation
            {
                Id = Guid.NewGuid(), PurchaseOrderId = IntegrationTestFixture.PoId,
                PoNumber = $"PO-NEG-{tag}", SupplierId = IntegrationTestFixture.SupplierId,
                NegotiationStatus = PoNegotiationStatus.Approved, PreviousPoStatus = PoStatus.Released,
                SubmittedAt = new DateTime(2026, 7, 3, 8, 0, 0, DateTimeKind.Utc),
                SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
            };
            neg.Lines.Add(new PurchaseOrderNegotiationLine
            {
                Id = Guid.NewGuid(), PurchaseOrderNegotiationId = neg.Id, PurchaseOrderLineId = IntegrationTestFixture.PoLine1Id,
                PositionNo = 10, SequenceNo = 1, ItemCode = "ITEM-A", OriginalQty = 100, NegotiatedQty = 80,
                OriginalDeliveryDate = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc),
                NegotiatedDeliveryDate = new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc),
                OriginalPrice = 10.50m, NegotiatedPrice = 9.75m, CreatedBy = "seed", CreatedOn = now,
            });
            neg.Lines.Add(new PurchaseOrderNegotiationLine
            {
                Id = Guid.NewGuid(), PurchaseOrderNegotiationId = neg.Id, PurchaseOrderLineId = IntegrationTestFixture.PoLine2Id,
                PositionNo = 20, SequenceNo = 2, ItemCode = "ITEM-B", OriginalQty = 50, NegotiatedQty = 50,
                OriginalDeliveryDate = null, NegotiatedDeliveryDate = null,
                OriginalPrice = 20m, NegotiatedPrice = 20m, CreatedBy = "seed", CreatedOn = now,
            });
            db.PurchaseOrderNegotiations.Add(neg);
            await db.SaveChangesAsync();
            negotiationId = neg.Id;
        }

        await AssertParityAsync(
            OutboxTransactionType.PoNegotiationApprove,
            new PoNegotiationInputDocumentBuilder(),
            negotiationId,
            db => PoNegotiationOutboundPayloadBuilder.BuildJsonAsync(db, negotiationId));
    }
}
