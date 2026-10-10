using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UnitEntity = MerinoOne.SupplierPortal.Domain.Entities.Mdm.Unit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// The unit master's <c>decimalPlaces</c> must round-trip EXACTLY — in particular 0, which is what a piece-counted unit
/// (EA, PCS, ...) needs.
///
/// <para><b>The bug these pin.</b> The model maps the column with <c>HasDefaultValue(2)</c>. EF Core treats the CLR default of
/// the property type (0 for an int) as "no value supplied" and OMITS the column from the INSERT, so the database default
/// lands: a unit saved with 0 decimals was stored as 2. The fix is <c>.HasSentinel(-1)</c> on the property (0 is now an
/// ordinary value that is written; the entity initialiser keeps an untouched unit at 2). Without the sentinel the 0-decimal
/// cases below read back 2.</para>
///
/// <para>Every test writes its OWN uniquely-coded unit and removes it afterwards, so the persistent test DB gains nothing and
/// no other test's unit is read or changed. <see cref="EnsureUnitAsync"/> is the shared, idempotent seeder the ASN and
/// delivery-schedule suites use for the units they need to exist.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class UnitMasterDecimalsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public UnitMasterDecimalsTests(IntegrationTestFixture fx) => _fx = fx;

    // ── the user-facing path: POST /api/masters/units → CreateUnitCommand ────────────────────────────────
    [SkippableFact]
    public async Task Unit_created_through_the_master_API_with_zero_decimals_reads_back_zero()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode("API0");
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        try
        {
            var create = await admin.PostAsJsonAsync("/api/masters/units", NewRequest(code, decimalPlaces: 0));
            create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
            var created = (await Read<UnitDto>(create)).Data!;
            created.DecimalPlaces.Should().Be(0, because: "a unit saved with 0 decimals must not be replaced by the column default (2)");

            var fetched = await admin.GetAsync($"/api/masters/units/{created.Id}");
            fetched.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(fetched));
            (await Read<UnitDto>(fetched)).Data!.DecimalPlaces.Should().Be(0);

            (await StoredDecimalsAsync(code)).Should().Be(0, because: "the stored column itself must hold 0, not just the DTO");
        }
        finally { await DeleteUnitAsync(code); }
    }

    // ── same path, a non-zero value, so the fix is proven not to disturb the ordinary case ───────────────
    [SkippableFact]
    public async Task Unit_created_through_the_master_API_keeps_a_non_zero_precision()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode("API4");
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        try
        {
            var create = await admin.PostAsJsonAsync("/api/masters/units", NewRequest(code, decimalPlaces: 4));
            create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
            (await Read<UnitDto>(create)).Data!.DecimalPlaces.Should().Be(4);
            (await StoredDecimalsAsync(code)).Should().Be(4);
        }
        finally { await DeleteUnitAsync(code); }
    }

    // ── the update path round-trips a move TO zero (and back) ────────────────────────────────────────────
    [SkippableFact]
    public async Task Unit_updated_to_zero_decimals_and_back_round_trips()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode("UPD");
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        try
        {
            var create = await admin.PostAsJsonAsync("/api/masters/units", NewRequest(code, decimalPlaces: 3));
            create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
            var id = (await Read<UnitDto>(create)).Data!.Id;
            (await StoredDecimalsAsync(code)).Should().Be(3);

            var toZero = await admin.PutAsJsonAsync($"/api/masters/units/{id}", NewUpdate(decimalPlaces: 0));
            toZero.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(toZero));
            (await StoredDecimalsAsync(code)).Should().Be(0);

            var back = await admin.PutAsJsonAsync($"/api/masters/units/{id}", NewUpdate(decimalPlaces: 2));
            back.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(back));
            (await StoredDecimalsAsync(code)).Should().Be(2);
        }
        finally { await DeleteUnitAsync(code); }
    }

    // ── the entity path every other writer uses (inbound ERP upsert, seeders): n decimals in → n out ─────
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task Unit_entity_saved_with_n_decimals_stores_exactly_n(int places)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode($"E{places}");
        try
        {
            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Units.Add(NewEntity(code, places));
                await db.SaveChangesAsync();
            }

            (await StoredDecimalsAsync(code)).Should().Be(places,
                because: "EF must write the supplied value, including 0, instead of leaving it to the column default");
        }
        finally { await DeleteUnitAsync(code); }
    }

    // ── a unit that never mentions decimals still gets the column default of 2 ───────────────────────────
    [SkippableFact]
    public async Task Unit_entity_that_never_sets_decimals_is_stored_with_the_default_of_two()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode("DEF");
        try
        {
            using (var scope = _fx.Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var untouched = NewEntity(code, places: null);
                untouched.DecimalPlaces.Should().Be(2, because: "the entity initialiser mirrors the column default");
                db.Units.Add(untouched);
                await db.SaveChangesAsync();
            }

            (await StoredDecimalsAsync(code)).Should().Be(2);
        }
        finally { await DeleteUnitAsync(code); }
    }

    // ── shared seeder ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Idempotent: makes sure a unit with this CODE exists under the fixture company and carries exactly
    /// <paramref name="decimalPlaces"/>. A create-if-missing that ALSO repairs a drifted value (UPDATE only when it differs),
    /// meant for the dedicated, namespaced <c>UT-*</c> codes a suite owns — it never touches any other unit.
    /// </summary>
    internal static async Task EnsureUnitAsync(IntegrationTestFixture fx, string code, int decimalPlaces)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var mine = db.Units.IgnoreQueryFilters()
            .Where(u => u.TenantEntityId == IntegrationTestFixture.CompanyId && u.Code == code && !u.IsDeleted);

        if (!await mine.AnyAsync())
        {
            db.Units.Add(NewEntity(code, decimalPlaces));
            await db.SaveChangesAsync();
        }

        await mine.Where(u => u.DecimalPlaces != decimalPlaces)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DecimalPlaces, decimalPlaces));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A unit code unique to this run (max 20 chars: "UT-MD-" + label + "-" + 6 hex).</summary>
    private static string NewCode(string label) => $"UT-MD-{label}-{Guid.NewGuid().ToString("N")[..6]}";

    private static UnitEntity NewEntity(string code, int? places)
    {
        var unit = new UnitEntity
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = "Unit master decimals test unit",
            UnitType = UnitType.Quantity,
            ConversionFactor = 1m,
            IsActive = true,
            TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed",   // short-circuits the audit interceptor, like every other fixture write
            CreatedOn = DateTime.UtcNow,
        };
        if (places is { } p) unit.DecimalPlaces = p;   // null = leave the entity initialiser alone
        return unit;
    }

    private static CreateUnitRequest NewRequest(string code, int decimalPlaces)
        => new(code, "Unit master decimals test unit", "Quantity", IsoCode: null, decimalPlaces, ConversionFactor: 1m, BaseUnitId: null);

    private static UpdateUnitRequest NewUpdate(int decimalPlaces)
        => new("Unit master decimals test unit", "Quantity", IsoCode: null, decimalPlaces, ConversionFactor: 1m, BaseUnitId: null, IsActive: true);

    private async Task<int> StoredDecimalsAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Units.IgnoreQueryFilters()
            .Where(u => u.Code == code && !u.IsDeleted)
            .Select(u => u.DecimalPlaces)
            .SingleAsync();
    }

    /// <summary>Hard-deletes this test's own unit (bulk DELETE: no tracker, no soft-delete interceptor).</summary>
    private async Task DeleteUnitAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Units.IgnoreQueryFilters().Where(u => u.Code == code).ExecuteDeleteAsync();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
