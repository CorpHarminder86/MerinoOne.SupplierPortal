using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.ApiKeys;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Regression for migration 0027 — the "Create API key" 500. <c>CreateApiKeyCommandHandler</c> stores
/// <c>string.Join(",", scopes)</c> into <c>integration.ApiKey.scopes</c>. That column was <c>nvarchar(400)</c>,
/// but the full <see cref="ApiKeyScopes.Allowed"/> set (18 tokens) joins to 509 chars, so minting a key with
/// every endpoint selected overflowed the column → SQL 2628 "String or binary data would be truncated" → 500.
///
/// <para>0027 widened the column to <c>nvarchar(max)</c>. This persists the exact maximal CSV the handler would
/// build (all allowed scopes) through a real DbContext and asserts it (a) saves without truncation and
/// (b) round-trips byte-identical. A guard asserts the payload actually exceeds the old 400 cap, so the test
/// stays meaningful and fails loudly if the column is ever re-narrowed.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ApiKeyScopesWidthTests
{
    private readonly IntegrationTestFixture _fx;
    public ApiKeyScopesWidthTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Minting_a_key_with_every_allowed_scope_is_not_truncated()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Exactly what the handler stores: every allowed scope, comma-joined.
        var scopesCsv = string.Join(",", ApiKeyScopes.Allowed);
        scopesCsv.Length.Should().BeGreaterThan(400,
            "this regression only has teeth if the maximal scope CSV exceeds the old nvarchar(400) cap " +
            "(if scopes were trimmed below that, widen-the-column is no longer the thing under test)");

        var id = Guid.NewGuid();
        var keyPrefix = ("mok_" + Guid.NewGuid().ToString("N"))[..20]; // unique, <= keyPrefix nvarchar(20)
        try
        {
            using (var s1 = _fx.Factory.Services.CreateScope())
            {
                var db1 = s1.ServiceProvider.GetRequiredService<AppDbContext>();
                db1.ApiKeys.Add(new ApiKey
                {
                    Id = id,
                    TenantId = IntegrationTestFixture.TenantId,
                    Label = "all-scopes width regression",
                    KeyPrefix = keyPrefix,
                    KeyHash = new string('a', 64), // char(64); value irrelevant to this test
                    Scopes = scopesCsv,
                    IsActive = true,
                    CreatedBy = "test",
                    CreatedOn = DateTime.UtcNow,
                });

                // Pre-0027 this threw DbUpdateException (SqlException 2628). Post-0027 it must succeed.
                await db1.SaveChangesAsync();
            }

            // Round-trips byte-identical (no silent truncation) in a fresh context.
            using (var s2 = _fx.Factory.Services.CreateScope())
            {
                var db2 = s2.ServiceProvider.GetRequiredService<AppDbContext>();
                var stored = await db2.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == id)
                    .Select(k => k.Scopes).SingleAsync();

                stored.Should().Be(scopesCsv);
                stored.Split(',').Should().HaveCount(ApiKeyScopes.Allowed.Count);
            }
        }
        finally
        {
            using var sc = _fx.Factory.Services.CreateScope();
            var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == id).ToListAsync();
            if (rows.Count > 0)
            {
                db.ApiKeys.RemoveRange(rows);
                await db.SaveChangesAsync();
            }
        }
    }
}
