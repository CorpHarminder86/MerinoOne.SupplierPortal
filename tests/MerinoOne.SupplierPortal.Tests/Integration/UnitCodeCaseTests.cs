using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UnitEntity = MerinoOne.SupplierPortal.Domain.Entities.Mdm.Unit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Unit codes are CASE-SENSITIVE (migration 0063: <c>mdm.Unit.code</c> is <c>SQL_Latin1_General_CP1_CS_AS</c>): "PCS" and
/// "pcs" are two different units, each with its own decimal places. Before the change the database's case-insensitive collation
/// folded them together, so the second one was refused as a duplicate.
///
/// <para>Every test owns its units: a code <c>Q&lt;tag&gt;</c> / <c>q&lt;tag&gt;</c> (and <c>X</c>/<c>x</c>, <c>W</c>/<c>w</c>, <c>Z</c>) where
/// the tag is a letter plus five hex digits, unique per run, so the codes fit the column, have an upper-case form that differs from
/// the lower-case one, and collide with nothing in the persistent test DB. Whatever a test creates it removes again (items first —
/// they reference units); only the one-off inbound API key and the sync-log rows an inbound push writes stay behind, as in
/// <c>TaxRateOverrideTests</c>.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class UnitCodeCaseTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IntegrationTestFixture _fx;
    public UnitCodeCaseTests(IntegrationTestFixture fx) => _fx = fx;

    // ── the user-facing path: both spellings are creatable, listable and resolve to their own decimals ───
    [SkippableFact]
    public async Task Both_spellings_of_a_unit_code_are_created_listed_and_resolved_as_separate_units()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        var upper = $"Q{tag}";
        var lower = $"q{tag}";
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        try
        {
            var createdUpper = await CreateUnitAsync(admin, upper, decimalPlaces: 0, "Upper-case spelling");
            var createdLower = await CreateUnitAsync(admin, lower, decimalPlaces: 2, "Lower-case spelling");   // not a duplicate of the upper-case one
            createdUpper.Code.Should().Be(upper, "the code is stored exactly as typed - no case folding on create");
            createdLower.Code.Should().Be(lower);

            // The very same spelling is still a duplicate.
            var duplicate = await admin.PostAsJsonAsync("/api/masters/units", NewRequest(lower, decimalPlaces: 1, "Same spelling again"));
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await Body(duplicate));

            // The list shows both. The SEARCH stays forgiving about case (it is a convenience, not an identity check).
            foreach (var search in new[] { tag, tag.ToUpperInvariant() })
            {
                var list = await admin.GetAsync($"/api/masters/units?search={search}");
                list.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(list));
                (await Read<List<UnitDto>>(list)).Data!.Select(u => u.Code)
                    .Should().BeEquivalentTo(new[] { upper, lower }, because: $"searching '{search}' finds both spellings");
            }

            // The resolver answers each spelling with its own unit's decimals ...
            using var scope = _fx.Factory.Services.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IUnitDecimalsResolver>();
            var ct = CancellationToken.None;
            (await resolver.ResolveAsync(upper, ct)).Should().Be(0, "an exact match wins over its case-variant");
            (await resolver.ResolveAsync(lower, ct)).Should().Be(2);

            // ... and a third spelling that matches neither exactly is ambiguous (two case-variants exist): fallback, not a guess.
            (await resolver.ResolveAsync($"Q{tag.ToUpperInvariant()}", ct)).Should().Be(resolver.Fallback);
        }
        finally { await DeleteUnitsAsync(upper, lower); }
    }

    // ── resolver fallbacks: the only case-variant answers; unknown is the fallback; keys are the requested spelling ──
    [SkippableFact]
    public async Task Resolver_falls_back_to_the_only_case_variant_and_to_three_when_there_is_none()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        var present = $"X{tag}";
        var asked = $"x{tag}";
        var unknown = $"Z{tag}";
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        try
        {
            await CreateUnitAsync(admin, present, decimalPlaces: 4, "Only spelling");

            using var scope = _fx.Factory.Services.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<IUnitDecimalsResolver>();
            var ct = CancellationToken.None;

            (await resolver.ResolveAsync(present, ct)).Should().Be(4, "an exact match");
            (await resolver.ResolveAsync(asked, ct)).Should().Be(4, "no unit is spelled like this, but X is its only case-variant");
            (await resolver.ResolveAsync(unknown, ct)).Should().Be(resolver.Fallback, "no spelling of the code exists at all");

            var map = await resolver.ResolveAsync(new string?[] { asked, unknown, null, "  " }, ct);
            map.Should().HaveCount(2);
            map[asked].Should().Be(4);
            map[unknown].Should().Be(resolver.Fallback);
            map.ContainsKey(present).Should().BeFalse("the result is keyed by the spelling that was requested, ordinal");
        }
        finally { await DeleteUnitsAsync(present); }
    }

    // ── the migration itself: collation, the unique index it had to drop and re-create, and what that index now allows ──
    [SkippableFact]
    public async Task Unit_code_column_is_case_sensitive_and_still_unique_per_company_after_the_migration()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        var upper = $"Q{tag}";
        var lower = $"q{tag}";
        try
        {
            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                (await db.Database.SqlQueryRaw<string>(
                        "SELECT COLLATION_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS " +
                        "WHERE TABLE_SCHEMA = N'mdm' AND TABLE_NAME = N'Unit' AND COLUMN_NAME = N'code'")
                    .SingleAsync())
                    .Should().Be("SQL_Latin1_General_CP1_CS_AS");

                // UQ_Unit_company_code was dropped around the ALTER COLUMN and must be back: unique, on (tenantEntityId, code),
                // filtered to live rows of a company.
                var index = await db.Database.SqlQueryRaw<string>(
                        "SELECT CONCAT(CASE WHEN i.is_unique = 1 THEN 'unique' ELSE 'non-unique' END, '|', ISNULL(i.filter_definition, N'')) AS [Value] " +
                        "FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'mdm.Unit') AND i.name = N'UQ_Unit_company_code'")
                    .SingleAsync();
                index.Should().StartWith("unique|");
                index.Should().Contain("tenantEntityId").And.Contain("isDeleted");

                var keyColumns = await db.Database.SqlQueryRaw<string>(
                        "SELECT CONCAT(RIGHT(CONCAT(N'00', ic.key_ordinal), 2), N':', c.name) AS [Value] " +
                        "FROM sys.index_columns ic " +
                        "JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id " +
                        "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                        "WHERE i.object_id = OBJECT_ID(N'mdm.Unit') AND i.name = N'UQ_Unit_company_code'")
                    .ToListAsync();
                keyColumns.OrderBy(k => k, StringComparer.Ordinal).Should().Equal("01:tenantEntityId", "02:code");

                // Two units that differ only by case coexist in one company ...
                db.Units.Add(NewEntity(upper, 0));
                db.Units.Add(NewEntity(lower, 2));
                await db.SaveChangesAsync();
            }

            // ... but the same spelling twice is still refused by the index.
            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Units.Add(NewEntity(lower, 3));

                Func<Task> save = async () => { await db.SaveChangesAsync(); };
                (await save.Should().ThrowAsync<DbUpdateException>())
                    .Which.GetBaseException().Message.Should().Contain("UQ_Unit_company_code");
            }
        }
        finally { await DeleteUnitsAsync(upper, lower); }
    }

    // ── LN inbound unit push: matching is exact, so one spelling never overwrites the other ──────────────
    [SkippableFact]
    public async Task Inbound_unit_push_of_one_spelling_never_touches_the_other_spelling()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        var upper = $"Q{tag}";
        var lower = $"q{tag}";
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var inbound = await CreateInboundClientAsync();
        try
        {
            await CreateUnitAsync(admin, upper, decimalPlaces: 0, "Upper-case spelling");

            // Push 1 - only "q<tag>": it is a NEW unit, and "Q<tag>" is left exactly as it was.
            var first = await PushUnitsAsync(inbound, $"u1-{tag}", Record(lower, "Lower pushed", decimalPlaces: 4));
            (first.Inserted, first.Updated, first.Failed).Should().Be((1, 0, 0), "a differently-cased code is not the existing unit");

            var q = await LoadUnitAsync(upper);
            q.Description.Should().Be("Upper-case spelling");
            q.DecimalPlaces.Should().Be(0);
            q.UpdatedBy.Should().BeNull("the push must not have touched the other spelling");
            var l = await LoadUnitAsync(lower);
            (l.Description, l.DecimalPlaces).Should().Be(("Lower pushed", 4));

            // Push 2 - "q<tag>" again with new values: updates the lower unit only.
            var second = await PushUnitsAsync(inbound, $"u2-{tag}", Record(lower, "Lower pushed again", decimalPlaces: 5));
            (second.Inserted, second.Updated, second.Failed).Should().Be((0, 1, 0));
            (await LoadUnitAsync(upper)).Description.Should().Be("Upper-case spelling");
            (await LoadUnitAsync(lower)).DecimalPlaces.Should().Be(5);

            // Push 3 - BOTH spellings in one batch: each updates its own unit, with no duplicate-key fold inside the upsert.
            var both = await PushUnitsAsync(inbound, $"u3-{tag}",
                Record(upper, "Upper batch", decimalPlaces: 1), Record(lower, "Lower batch", decimalPlaces: 6));
            (both.Inserted, both.Updated, both.Failed).Should().Be((0, 2, 0));
            var qb = await LoadUnitAsync(upper);
            var lb = await LoadUnitAsync(lower);
            (qb.Description, qb.DecimalPlaces).Should().Be(("Upper batch", 1));
            (lb.Description, lb.DecimalPlaces).Should().Be(("Lower batch", 6));
        }
        finally { await DeleteUnitsAsync(upper, lower); }
    }

    // ── a batch carrying BOTH spellings of a brand-new code creates two units (no in-batch case fold) ────
    [SkippableFact]
    public async Task Inbound_unit_push_creates_both_spellings_of_a_new_code_from_one_batch()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        var upper = $"W{tag}";
        var lower = $"w{tag}";
        var inbound = await CreateInboundClientAsync();
        try
        {
            // Neither exists. Were the in-memory "already seen" map case-insensitive, the second row would be taken for an update
            // of the first one's pending insert and only one unit would be created.
            var result = await PushUnitsAsync(inbound, $"u4-{tag}",
                Record(upper, "Upper in batch", decimalPlaces: 0), Record(lower, "Lower in batch", decimalPlaces: 2));
            (result.Inserted, result.Updated, result.Failed).Should().Be((2, 0, 0));

            (await LoadUnitAsync(upper)).DecimalPlaces.Should().Be(0);
            (await LoadUnitAsync(lower)).DecimalPlaces.Should().Be(2);
        }
        finally { await DeleteUnitsAsync(upper, lower); }
    }

    // ── the idempotency hash must not fold case either: no header, same payload but for the code's case ──
    [SkippableFact]
    public async Task Inbound_unit_push_without_an_idempotency_key_is_not_replayed_for_the_other_spelling()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        var upper = $"Q{tag}";
        var lower = $"q{tag}";
        var inbound = await CreateInboundClientAsync();
        try
        {
            var first = await PushUnitsAsync(inbound, idempotencyKey: null, Record(upper, "Same payload", decimalPlaces: 3));
            (first.Inserted, first.Skipped).Should().Be((1, 0));

            // Identical in every field but the code's case. Were the code upper-cased into the canonical hash, this would
            // hash like the first push and come back as a replayed no-op (all rows Skipped) without creating anything.
            var second = await PushUnitsAsync(inbound, idempotencyKey: null, Record(lower, "Same payload", decimalPlaces: 3));
            (second.Inserted, second.Skipped, second.Failed).Should().Be((1, 0, 0), "'q' is another unit, so this is not a replay of the 'Q' push");
            await LoadUnitAsync(lower);   // it exists

            // The very same payload again IS a replay, as before.
            var replay = await PushUnitsAsync(inbound, idempotencyKey: null, Record(lower, "Same payload", decimalPlaces: 3));
            (replay.Inserted, replay.Updated, replay.Skipped).Should().Be((0, 0, 1));
        }
        finally { await DeleteUnitsAsync(upper, lower); }
    }

    // ── LN inbound item push: the item's unit code is matched exactly too ───────────────────────────────
    [SkippableFact]
    public async Task Inbound_item_push_links_the_unit_spelled_exactly_like_its_unit_code()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = NewTag();
        var upper = $"Q{tag}";
        var lower = $"q{tag}";
        var itemCode = $"UT-CASE-{tag}";
        var itemOnUpper = $"UT-CASEU-{tag}";
        var itemOnLower = $"UT-CASEL-{tag}";
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var inbound = await CreateInboundClientAsync();
        try
        {
            var upperId = (await CreateUnitAsync(admin, upper, decimalPlaces: 0, "Upper-case spelling")).Id;
            var lowerId = (await CreateUnitAsync(admin, lower, decimalPlaces: 2, "Lower-case spelling")).Id;

            // The item points at the lower-case unit ...
            var onLower = await PushItemAsync(inbound, $"i1-{tag}", itemCode, lower);
            (onLower.Inserted, onLower.Failed).Should().Be((1, 0));
            (await LoadItemUnitIdAsync(itemCode)).Should().Be(lowerId, "unit code 'q...' is the lower-case unit, not its upper-case twin");

            // ... then, re-pushed with the upper-case code, at the upper-case one ...
            var onUpper = await PushItemAsync(inbound, $"i2-{tag}", itemCode, upper);
            (onUpper.Updated, onUpper.Failed).Should().Be((1, 0));
            (await LoadItemUnitIdAsync(itemCode)).Should().Be(upperId);

            // ... and a spelling no unit has is refused (two other spellings exist, but neither is it), the item untouched.
            var neither = await PushItemAsync(inbound, $"i3-{tag}", itemCode, $"Q{tag.ToUpperInvariant()}");
            (neither.Updated, neither.Failed).Should().Be((0, 1));
            neither.Rows.Single().Error.Should().Contain("Unknown unit code");
            (await LoadItemUnitIdAsync(itemCode)).Should().Be(upperId);

            // One batch, two items on the two spellings: each gets its own unit (the batch-level lookup keeps both spellings apart).
            var batch = await PushItemsAsync(inbound, $"i4-{tag}", (itemOnUpper, upper), (itemOnLower, lower));
            (batch.Inserted, batch.Failed).Should().Be((2, 0));
            (await LoadItemUnitIdAsync(itemOnUpper)).Should().Be(upperId);
            (await LoadItemUnitIdAsync(itemOnLower)).Should().Be(lowerId);
        }
        finally
        {
            await DeleteItemsAsync(itemCode);
            await DeleteItemsAsync(itemOnUpper);
            await DeleteItemsAsync(itemOnLower);
            await DeleteUnitsAsync(upper, lower);
        }
    }

    // -------------------- helpers --------------------

    /// <summary>A letter plus five hex digits: 6 characters, unique per run, with an upper-case form that differs from the lower-case one.</summary>
    private static string NewTag() => "k" + Guid.NewGuid().ToString("N")[..5];

    private static CreateUnitRequest NewRequest(string code, int decimalPlaces, string description)
        => new(code, description, "Quantity", IsoCode: null, decimalPlaces, ConversionFactor: 1m, BaseUnitId: null);

    /// <summary>Creates a unit through the master API (<c>POST /api/masters/units</c>), failing with the response body if it is refused.</summary>
    private static async Task<UnitDto> CreateUnitAsync(HttpClient admin, string code, int decimalPlaces, string description)
    {
        var resp = await admin.PostAsJsonAsync("/api/masters/units", NewRequest(code, decimalPlaces, description));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: $"unit '{code}' must be creatable: " + await Body(resp));
        return (await Read<UnitDto>(resp)).Data!;
    }

    private static UnitRecord Record(string code, string description, int decimalPlaces)
        => new(code, description, "Quantity", IsoCode: null, decimalPlaces, ConversionFactor: 1m, BaseUnitCode: null, IsActive: true);

    private static UnitEntity NewEntity(string code, int places) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Description = "Unit code case test unit",
        UnitType = UnitType.Quantity,
        DecimalPlaces = places,
        ConversionFactor = 1m,
        IsActive = true,
        TenantId = IntegrationTestFixture.TenantId,
        TenantEntityId = IntegrationTestFixture.CompanyId,
        CreatedBy = "seed",   // short-circuits the audit interceptor, like every other fixture write
        CreatedOn = DateTime.UtcNow,
    };

    /// <summary>The live unit with exactly this code under the fixture company (the column is case-sensitive: an exact read).</summary>
    private async Task<UnitEntity> LoadUnitAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Units.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.TenantEntityId == IntegrationTestFixture.CompanyId && u.Code == code && !u.IsDeleted);
    }

    private async Task<Guid?> LoadItemUnitIdAsync(string itemCode)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.TenantEntityId == IntegrationTestFixture.CompanyId && i.Code == itemCode && !i.IsDeleted)
            .Select(i => i.UnitId)
            .SingleAsync();
    }

    /// <summary>Hard-deletes this test's own units, one exact code at a time (bulk DELETE: no tracker, no soft-delete interceptor).</summary>
    private async Task DeleteUnitsAsync(params string[] codes)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var code in codes)
            await db.Units.IgnoreQueryFilters().Where(u => u.Code == code).ExecuteDeleteAsync();
    }

    private async Task DeleteItemsAsync(string itemCode)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Items.IgnoreQueryFilters().Where(i => i.Code == itemCode).ExecuteDeleteAsync();
    }

    /// <summary>
    /// Seeds the "Unit" and "Item" inbound endpoint maps (if missing) + a one-off API key carrying both scopes, bound to the
    /// fixture company. The fixture's own key carries neither scope, so this suite mints its own (like <c>TaxRateOverrideTests</c>).
    /// </summary>
    private async Task<HttpClient> CreateInboundClientAsync()
    {
        var keyTag = Guid.NewGuid().ToString("N")[..8];
        var plaintext = $"mok_{keyTag}unitcasesecretkey00000000000";
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            foreach (var (entity, url, bod) in new[] { ("Unit", "units", "SyncUnit"), ("Item", "items", "SyncItem") })
            {
                if (await db.InforEndpointMaps.IgnoreQueryFilters().AnyAsync(m =>
                        !m.IsDeleted && m.TenantId == IntegrationTestFixture.TenantId
                        && m.EntityName == entity && m.Direction == SyncDirection.Inbound))
                    continue;

                db.InforEndpointMaps.Add(new InforEndpointMap
                {
                    Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId, EntityName = entity,
                    Direction = SyncDirection.Inbound, InforEndpointUrl = $"/api/integration/inbound/{url}",
                    BodName = bod, IsEnabled = true, ReceivedCount = 0, CreatedBy = "seed", CreatedOn = now,
                });
            }

            var keyId = Guid.NewGuid();
            db.ApiKeys.Add(new ApiKey
            {
                Id = keyId,
                TenantId = IntegrationTestFixture.TenantId,
                Label = $"Unit-case test key {keyTag}",
                KeyPrefix = plaintext[..IntegrationTestFixture.ApiKeyPrefixLength],
                KeyHash = ApiKeyHasher.Hash(plaintext),
                Scopes = "Integration.Inbound.Unit Integration.Inbound.Item",
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now,
            });
            db.ApiKeyCompanies.Add(new ApiKeyCompany
            {
                Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId, ApiKeyId = keyId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = now,
            });
            await db.SaveChangesAsync();
        }

        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-APIKey", plaintext);
        return client;
    }

    private static Task<UpsertResultDto> PushUnitsAsync(HttpClient inbound, string? idempotencyKey, params UnitRecord[] records)
        => PostInboundAsync(inbound, "/api/integration/inbound/units",
            new PushUnitsRequest(IntegrationTestFixture.CompanyCode, records), idempotencyKey);

    private static Task<UpsertResultDto> PushItemAsync(HttpClient inbound, string idempotencyKey, string itemCode, string unitCode)
        => PushItemsAsync(inbound, idempotencyKey, (itemCode, unitCode));

    private static Task<UpsertResultDto> PushItemsAsync(HttpClient inbound, string idempotencyKey, params (string ItemCode, string UnitCode)[] items)
        => PostInboundAsync(inbound, "/api/integration/inbound/items",
            new PushItemsRequest(IntegrationTestFixture.CompanyCode, items
                .Select(i => new ItemRecord(i.ItemCode, $"Unit-case test item {i.ItemCode}", i.UnitCode, ItemGroupCode: null, HsnCode: null))
                .ToList()),
            idempotencyKey);

    private static async Task<UpsertResultDto> PostInboundAsync(HttpClient inbound, string url, object body, string? idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        var resp = await inbound.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<UpsertResultDto>(resp)).Data!;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
