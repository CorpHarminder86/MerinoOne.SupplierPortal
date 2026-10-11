using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UnitEntity = MerinoOne.SupplierPortal.Domain.Entities.Mdm.Unit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// <see cref="IUnitDecimalsResolver"/> against the real <c>mdm.Unit</c> table. Two dedicated, namespaced units (0 and 5
/// decimal places) are seeded once under the fixture company and then LEFT in the persistent test DB: an idempotent
/// create-if-missing that never mutates or removes anything, on codes no other test reads.
///
/// <para>The resolver is resolved from a fresh scope, i.e. under the system principal (filters bypassed), exactly like the
/// other DB-backed tests; the contract under test is the code-to-decimals mapping. Unit codes are case-sensitive (migration
/// 0063), so a code resolves EXACT-first; a code with no unit spelled exactly like it but exactly ONE case-variant in the master
/// falls back to that unit; unknown/blank codes (and ambiguous ones) get the fallback 3; and the batch overload's result keys are
/// the codes AS REQUESTED, ordinal. The two-spellings and ambiguous cases live in <see cref="UnitCodeCaseTests"/>.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class UnitDecimalsResolverTests
{
    private const string ZeroDecimalsCode = "UT-DECIMALS-0";
    private const string FiveDecimalsCode = "UT-DECIMALS-5";
    private const string UnknownCode = "UT-NO-SUCH-UNIT";

    private readonly IntegrationTestFixture _fx;
    public UnitDecimalsResolverTests(IntegrationTestFixture fx) => _fx = fx;

    private async Task SeedUnitsAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (code, places) in new[] { (ZeroDecimalsCode, 0), (FiveDecimalsCode, 5) })
        {
            var mine = db.Units.IgnoreQueryFilters()
                .Where(u => u.TenantEntityId == IntegrationTestFixture.CompanyId && u.Code == code && !u.IsDeleted);

            if (!await mine.AnyAsync())
            {
                db.Units.Add(new UnitEntity
                {
                    Id = Guid.NewGuid(),
                    Code = code,
                    Description = "Unit decimals resolver test unit",
                    UnitType = UnitType.Quantity,
                    DecimalPlaces = places,
                    ConversionFactor = 1m,
                    IsActive = true,
                    TenantId = IntegrationTestFixture.TenantId,
                    TenantEntityId = IntegrationTestFixture.CompanyId,
                    CreatedBy = "test",
                    CreatedOn = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            // Pin the value with an explicit UPDATE. The model maps unit.decimalPlaces with HasDefaultValue(2), so EF omits
            // a CLR-default 0 from the INSERT above and the database default (2) lands instead; this makes the 0-decimal
            // unit really be 0 (and repairs a row left at 2 by an earlier run).
            await mine.Where(u => u.DecimalPlaces != places)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DecimalPlaces, places));
        }
    }

    [SkippableFact]
    public async Task Seeded_unit_resolves_to_its_decimal_places_by_exact_code_then_by_its_only_case_variant()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedUnitsAsync();

        using var scope = _fx.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IUnitDecimalsResolver>();
        var ct = CancellationToken.None;

        (await resolver.ResolveAsync(ZeroDecimalsCode, ct)).Should().Be(0, "a 0-decimal unit must read 0, not the fallback");
        (await resolver.ResolveAsync(FiveDecimalsCode, ct)).Should().Be(5);

        // No unit is spelled like these, but each master code has exactly ONE case-variant, so that unit answers.
        (await resolver.ResolveAsync(ZeroDecimalsCode.ToLowerInvariant(), ct)).Should().Be(0,
            "with no exact match, the code's only case-variant in the master answers");
        (await resolver.ResolveAsync("Ut-Decimals-5", ct)).Should().Be(5, "mixed case falls back to the only case-variant too");
        (await resolver.ResolveAsync($"  {ZeroDecimalsCode.ToLowerInvariant()}  ", ct)).Should().Be(0, "surrounding blanks are ignored");
    }

    [SkippableFact]
    public async Task Unknown_blank_and_null_codes_resolve_to_the_fallback_of_three()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedUnitsAsync();

        using var scope = _fx.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IUnitDecimalsResolver>();
        var ct = CancellationToken.None;

        resolver.Fallback.Should().Be(3);
        (await resolver.ResolveAsync(UnknownCode, ct)).Should().Be(3);
        (await resolver.ResolveAsync("", ct)).Should().Be(3);
        (await resolver.ResolveAsync("   ", ct)).Should().Be(3);
        (await resolver.ResolveAsync((string?)null, ct)).Should().Be(3);
    }

    [SkippableFact]
    public async Task Batch_overload_maps_every_requested_code_under_the_spelling_it_was_requested_with()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedUnitsAsync();

        using var scope = _fx.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IUnitDecimalsResolver>();

        var map = await resolver.ResolveAsync(
            new string?[] { ZeroDecimalsCode, FiveDecimalsCode.ToLowerInvariant(), UnknownCode, null, "", ZeroDecimalsCode.ToLowerInvariant() },
            CancellationToken.None);

        map[ZeroDecimalsCode].Should().Be(0, "an exact match");
        map[FiveDecimalsCode.ToLowerInvariant()].Should().Be(5, "no unit is spelled like this, so the code's only case-variant answers");
        map[ZeroDecimalsCode.ToLowerInvariant()].Should().Be(0);
        map[UnknownCode].Should().Be(3, "an unknown code maps to the fallback");
        map.Should().HaveCount(4, "null/blank codes are omitted, and every distinct spelling of a code is its own entry");

        // The keys are ordinal: a spelling that was not requested is not there, even though its case-variant is.
        map.ContainsKey(FiveDecimalsCode).Should().BeFalse("only the lower-case spelling of the five-decimal unit was requested");
    }

    [SkippableFact]
    public async Task Batch_overload_with_no_usable_codes_returns_an_empty_map()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        using var scope = _fx.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IUnitDecimalsResolver>();

        (await resolver.ResolveAsync(Array.Empty<string?>(), CancellationToken.None)).Should().BeEmpty();
        (await resolver.ResolveAsync(new string?[] { null, "", "  " }, CancellationToken.None)).Should().BeEmpty();
    }
}
