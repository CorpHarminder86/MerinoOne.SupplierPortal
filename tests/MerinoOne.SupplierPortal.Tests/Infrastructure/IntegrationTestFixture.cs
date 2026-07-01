using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;

namespace MerinoOne.SupplierPortal.Tests.Infrastructure;

/// <summary>
/// One-time DB stand-up + minimal seed shared across the integration tests (xUnit collection fixture).
///
/// <para>Migrates the dedicated test DB then seeds the MINIMUM topology the GRN-status / auto-post path needs:
/// a tenant + one company (code "2000") + the inbound endpoint maps + an X-APIKey bound to that company with the
/// transactional scopes, then a supplier + PO (two lines) + ASN + a Submitted invoice + two GoodsReceipts (one
/// per PO position) under ONE GrnNumber so the multi-row regression is exercised. All ids are deterministic and
/// every write carries <c>CreatedBy="seed"</c> (the audit interceptor short-circuits) and stamps the scope
/// columns explicitly (the fixture runs under the system principal, so the stamp interceptor is bypassed).</para>
///
/// <para>If the test DB cannot be reached/created (<see cref="DbAvailable"/> = false), the integration tests
/// skip with a clear message; the seed never throws the whole collection down.</para>
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    public CustomWebApplicationFactory Factory { get; private set; } = default!;

    /// <summary>False when the SQL-Express test DB could not be reached/migrated in this environment.</summary>
    public bool DbAvailable { get; private set; }

    /// <summary>The reason the DB could not be stood up (surfaced in skipped-test diagnostics).</summary>
    public string? DbUnavailableReason { get; private set; }

    // ---- Plaintext inbound API key. The stored KeyPrefix is the first 12 chars (= "mok_" + 8), which the
    // ApiKeyAuthenticationHandler uses for the O(1) lookup before the constant-time hash compare.
    public const string ApiKeyPlaintext = "mok_inttest0integrationtestsecretkey0001";
    public const int ApiKeyPrefixLength = 12; // matches ApiKeyAuthenticationHandler.PrefixLength
    public const string CompanyCode = "2000";

    // R5 (TSD R5 Addendum §6.2) — the inbound PO ship-to CODE the tests push. Resolves to the seeded
    // admin.CompanyAddress.erpCode below; the resolution populates PurchaseOrder.shipToAddressId + the snapshot.
    public const string ShipToErpCode = "DC-TEST-01";

    // Deterministic ids (kept stable so the seed is idempotent across runs).
    public static readonly Guid TenantId      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CompanyId     = Guid.Parse("22222222-2222-2222-2222-222222222222");
    // R5 ([[r5-consolidation]]) — a named ship-to address hung off the fixture company (TenantEntity CompanyId);
    // its erpCode (ShipToErpCode) is what the inbound PO push resolves against.
    public static readonly Guid ShipToAddressId = Guid.Parse("2c000000-0000-0000-0000-000000000002");
    public static readonly Guid SupplierId    = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid SeccodeId     = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid ApiKeyId      = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid PoId          = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid PoLine1Id     = Guid.Parse("66666666-6666-6666-6666-000000000001");
    public static readonly Guid PoLine2Id     = Guid.Parse("66666666-6666-6666-6666-000000000002");
    public static readonly Guid AsnId         = Guid.Parse("77777777-7777-7777-7777-777777777777");
    public static readonly Guid InvoiceId     = Guid.Parse("88888888-8888-8888-8888-888888888888");
    public static readonly Guid Grn1Id        = Guid.Parse("99999999-9999-9999-9999-000000000001");
    public static readonly Guid Grn2Id        = Guid.Parse("99999999-9999-9999-9999-000000000002");

    // The GRN number shared by BOTH receipt rows (one per PO position) — the multi-row regression key.
    public const string GrnNumber   = "GRN-INT-0001";
    public const string AsnNumber   = "ASN-INT-0001";
    public const string PoNumber    = "PO-INT-0001";
    public const string InvoiceNumber = "INV-INT-0001";

    public async Task InitializeAsync()
    {
        Factory = new CustomWebApplicationFactory();

        try
        {
            // Force the host to build (and run the startup MigrateAsync) by resolving a scope from Services.
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Belt-and-suspenders: explicitly migrate in case the startup hook is ever skipped.
            await db.Database.MigrateAsync();

            await SeedAsync(db);

            // Phase-1 security + RLS harness: roles/permissions, login-able users (Admin/Supplier/Buyer),
            // and a second tenant with foreign supplier/sync-log/document rows. Layered on top of the
            // money-path seed; never mutates it. See SecurityTestHarness for the gate-flip strategy.
            await SecurityTestHarness.SeedAsync(db);

            // R5 — drop the singleton PO-status-map cache so it reloads the just-seeded mapping rows on the next
            // inbound resolution (the singleton may have lazily loaded an empty map against a reused test DB).
            foreach (var inv in Factory.Services.GetServices<MerinoOne.SupplierPortal.Application.SystemSettings.ISettingsCacheInvalidator>())
                inv.InvalidateCategory(MerinoOne.SupplierPortal.Application.PurchaseOrders.StatusMapping.PoStatusMapService.Category);

            DbAvailable = true;
        }
        catch (Exception ex)
        {
            DbAvailable = false;
            DbUnavailableReason = ex.GetBaseException().Message;
        }
    }

    public Task DisposeAsync()
    {
        Factory?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>An HttpClient pre-loaded with the inbound X-APIKey header.</summary>
    public HttpClient CreateInboundClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-APIKey", ApiKeyPlaintext);
        return client;
    }

    private static async Task SeedAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;

        // --- Tenant ---------------------------------------------------------------------------------------
        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == TenantId))
        {
            db.Tenants.Add(new Tenant { Id = TenantId, Name = "IntTest Tenant", IsActive = true, CreatedBy = "seed", CreatedOn = now });
            await db.SaveChangesAsync();
        }

        // --- Company (TenantEntity, code "2000") -----------------------------------------------------------
        if (!await db.TenantEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == CompanyId))
        {
            db.TenantEntities.Add(new TenantEntity
            {
                Id = CompanyId, TenantId = TenantId, Code = CompanyCode, Name = "IntTest Co 2000",
                IsActive = true, CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }

        // --- Inbound endpoint maps (kill-switch gate; Direction=Inbound, IsEnabled=true) -------------------
        var inboundEntities = new[]
        {
            nameof(TransactionalInboundEntity.Grn),
            nameof(TransactionalInboundEntity.GrnReceipt),
            nameof(TransactionalInboundEntity.Po),
            nameof(TransactionalInboundEntity.Payment),
            nameof(TransactionalInboundEntity.InvoiceStatus),
            nameof(TransactionalInboundEntity.ErpAck),
        };
        var existingMaps = await db.InforEndpointMaps.IgnoreQueryFilters()
            .Where(m => m.TenantId == TenantId && m.Direction == SyncDirection.Inbound)
            .Select(m => m.EntityName).ToListAsync();
        foreach (var en in inboundEntities)
        {
            if (existingMaps.Contains(en)) continue;
            db.InforEndpointMaps.Add(new InforEndpointMap
            {
                Id = Guid.NewGuid(), TenantId = TenantId, EntityName = en, Direction = SyncDirection.Inbound,
                InforEndpointUrl = $"/api/integration/inbound/{en}", BodName = $"Sync{en}", IsEnabled = true,
                ReceivedCount = 0, CreatedBy = "seed", CreatedOn = now
            });
        }
        await db.SaveChangesAsync();

        // --- API key bound to company "2000" with the transactional scopes --------------------------------
        if (!await db.ApiKeys.IgnoreQueryFilters().AnyAsync(k => k.Id == ApiKeyId))
        {
            db.ApiKeys.Add(new ApiKey
            {
                Id = ApiKeyId,
                TenantId = TenantId,
                Label = "IntTest inbound key",
                KeyPrefix = ApiKeyPlaintext[..ApiKeyPrefixLength],
                KeyHash = ApiKeyHasher.Hash(ApiKeyPlaintext),
                Scopes = string.Join(' ',
                    "Integration.Inbound.Grn",
                    "Integration.Inbound.GrnReceipt",
                    "Integration.Inbound.Po",
                    "Integration.Inbound.Payment",
                    "Integration.Inbound.InvoiceStatus",
                    "Integration.Inbound.ErpAck"),
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            });
            db.ApiKeyCompanies.Add(new ApiKeyCompany
            {
                Id = Guid.NewGuid(), TenantId = TenantId, ApiKeyId = ApiKeyId, TenantEntityId = CompanyId,
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }

        // --- Seccode (type G) for the supplier's RLS owner ------------------------------------------------
        if (!await db.Seccodes.IgnoreQueryFilters().AnyAsync(s => s.Id == SeccodeId))
        {
            db.Seccodes.Add(new Seccode
            {
                Id = SeccodeId, SeccodeType = SeccodeType.G, Name = "IntTest supplier seccode",
                SupplierId = SupplierId, TenantId = TenantId, TenantEntityId = CompanyId,
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }

        // --- Ship-to address (R5 §4.2 / [[r5-consolidation]]) ----------------------------------------------
        // A named ship-to address hung directly off the fixture company (TenantEntity CompanyId), whose erpCode
        // (ShipToErpCode) the inbound PO push resolves against. CompanyAddress is an AuditableEntity with no
        // tenant/seccode column of its own; the always-on filters are bypassed at read time anyway.
        if (!await db.CompanyAddresses.IgnoreQueryFilters().AnyAsync(a => a.Id == ShipToAddressId))
        {
            db.CompanyAddresses.Add(new CompanyAddress
            {
                Id = ShipToAddressId, TenantEntityId = CompanyId, AddressName = "IntTest DC",
                ErpCode = ShipToErpCode, AddressType = "Shipping", AddressLine1 = "1 Test Estate",
                City = "Mumbai", State = "Maharashtra", Pincode = "400001", Country = "India",
                IsActive = true, CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }

        // --- ERP→portal PO status mapping (R5 §4.7 / §11) -------------------------------------------------
        // The inbound-PO status derivation (§11.3) resolves the raw erpStatus through this map; without it every
        // erpStatus would be UNMAPPED. Seed the signed-off default so the resolver-driven PoStatus matches the
        // pushed status in the inbound tests (e.g. erpStatus "Released" → portal Released). Owned by the supplier
        // seccode (type G) so the FK is valid under the system-principal seed.
        if (!await db.PoStatusMappings.IgnoreQueryFilters().AnyAsync(m => m.TenantId == TenantId))
        {
            var seed = new (string Erp, PoStatus Po)[]
            {
                ("Draft", PoStatus.Draft), ("Created", PoStatus.Draft),
                ("Approved", PoStatus.Released), ("Released", PoStatus.Released),
                ("Sent", PoStatus.Released), ("modified", PoStatus.Released),
                ("Canceled", PoStatus.Cancelled), ("Blocked", PoStatus.Cancelled),
                ("Closed", PoStatus.Closed), ("Delivered", PoStatus.Delivered),
            };
            foreach (var (erp, po) in seed)
                db.PoStatusMappings.Add(new PoStatusMapping
                {
                    Id = Guid.NewGuid(), TenantId = TenantId, ErpStatus = erp, PoStatus = po.ToString(),
                    IsActive = true, SeccodeId = SeccodeId, CreatedBy = "seed", CreatedOn = now
                });
            await db.SaveChangesAsync();
        }

        // --- Supplier -------------------------------------------------------------------------------------
        if (!await db.Suppliers.IgnoreQueryFilters().AnyAsync(s => s.Id == SupplierId))
        {
            db.Suppliers.Add(new SupplierEntity
            {
                Id = SupplierId, SupplierCode = "SUP-INT-01", LegalName = "IntTest Supplier Pvt Ltd",
                SupplierType = SupplierType.Material, RegistrationStatus = RegistrationStatus.Active,
                IsActiveSupplier = true, SeccodeId = SeccodeId, TenantId = TenantId, TenantEntityId = CompanyId,
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }

        // --- Purchase order + two lines (two PO positions) ------------------------------------------------
        if (!await db.PurchaseOrders.IgnoreQueryFilters().AnyAsync(p => p.Id == PoId))
        {
            db.PurchaseOrders.Add(new PurchaseOrder
            {
                Id = PoId, PoNumber = PoNumber, SupplierId = SupplierId, PoType = PoType.Material,
                PoDate = now.Date, PoStatus = PoStatus.Released, SeccodeId = SeccodeId,
                TenantId = TenantId, TenantEntityId = CompanyId, CreatedBy = "seed", CreatedOn = now
            });
            db.PurchaseOrderLines.Add(new PurchaseOrderLine
            {
                Id = PoLine1Id, PurchaseOrderId = PoId, PositionNo = 10, SequenceNo = 1, ItemCode = "ITEM-A",
                OrderUnit = "EA", OrderQty = 100, PriceUnit = 1, Price = 10, CreatedBy = "seed", CreatedOn = now
            });
            db.PurchaseOrderLines.Add(new PurchaseOrderLine
            {
                Id = PoLine2Id, PurchaseOrderId = PoId, PositionNo = 20, SequenceNo = 2, ItemCode = "ITEM-B",
                OrderUnit = "EA", OrderQty = 50, PriceUnit = 1, Price = 20, CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }

        // --- ASN (links the GRNs to the invoice deterministically) ----------------------------------------
        if (!await db.Asns.IgnoreQueryFilters().AnyAsync(a => a.Id == AsnId))
        {
            db.Asns.Add(new Asn
            {
                Id = AsnId, AsnNumber = AsnNumber, PurchaseOrderId = PoId, SupplierId = SupplierId,
                ExpectedDeliveryDate = now.Date.AddDays(1), AsnStatus = AsnStatus.Submitted,
                SeccodeId = SeccodeId, TenantId = TenantId, TenantEntityId = CompanyId,
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }

        // --- Submitted invoice on that ASN (the auto-post target) -----------------------------------------
        if (!await db.Invoices.IgnoreQueryFilters().AnyAsync(i => i.Id == InvoiceId))
        {
            db.Invoices.Add(new Invoice
            {
                Id = InvoiceId, InvoiceNumber = InvoiceNumber, PurchaseOrderId = PoId, AsnId = AsnId,
                SupplierId = SupplierId, InvoiceDate = now.Date, InvoiceAmount = 1500, TaxAmount = 0,
                NetAmount = 1500, CurrencyCode = "INR", MatchingType = MatchingType.ThreeWay,
                InvoiceStatus = InvoiceStatus.Submitted, SubmittedAt = now, SubmittedBy = "seed",
                SeccodeId = SeccodeId, TenantId = TenantId, TenantEntityId = CompanyId,
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }

        // --- Two GoodsReceipts under ONE GrnNumber (one row per PO position), linked to the invoice --------
        // GrnNotApproved so the grn-status push can transition them INTO GrnApproved and trigger the cascade.
        if (!await db.GoodsReceipts.IgnoreQueryFilters().AnyAsync(g => g.Id == Grn1Id))
        {
            db.GoodsReceipts.Add(new GoodsReceipt
            {
                Id = Grn1Id, GrnNumber = GrnNumber, PurchaseOrderLineId = PoLine1Id, AsnId = AsnId,
                InvoiceId = InvoiceId, ReceivedQty = 100, GrnDate = now.Date, GrnStatus = GrnStatus.GrnNotApproved,
                SeccodeId = SeccodeId, TenantId = TenantId, TenantEntityId = CompanyId,
                CreatedBy = "seed", CreatedOn = now
            });
            db.GoodsReceipts.Add(new GoodsReceipt
            {
                Id = Grn2Id, GrnNumber = GrnNumber, PurchaseOrderLineId = PoLine2Id, AsnId = AsnId,
                InvoiceId = InvoiceId, ReceivedQty = 50, GrnDate = now.Date, GrnStatus = GrnStatus.GrnNotApproved,
                SeccodeId = SeccodeId, TenantId = TenantId, TenantEntityId = CompanyId,
                CreatedBy = "seed", CreatedOn = now
            });
            await db.SaveChangesAsync();
        }
    }
}

/// <summary>xUnit collection so the migrate+seed runs ONCE for all integration tests.</summary>
[CollectionDefinition(IntegrationCollection.Name)]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationTestFixture>
{
    public const string Name = "Integration DB collection";
}
