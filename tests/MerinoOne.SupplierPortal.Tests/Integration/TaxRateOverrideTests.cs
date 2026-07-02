using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
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

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R6 — the admin tax-rate OVERRIDE vs the LN tax sync: an admin rate edit pins the row
/// (<c>isRateOverridden</c>); the inbound <c>/taxes</c> sync then skips writing <c>taxRate</c> for that row but
/// ALWAYS updates <c>lastSyncedRate</c>; <c>ResetRateOverride</c> restores the synced rate. The fixture API key
/// does not carry the Tax scope, so this suite seeds its own Tax-scoped key + the "Tax" inbound endpoint map.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TaxRateOverrideTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public TaxRateOverrideTests(IntegrationTestFixture fx) => _fx = fx;

    // ── (j) LN re-sync skips the overridden row's rate; lastSyncedRate ALWAYS updates ──────────────────
    // ── (k) admin rate edit sets the pin; ResetRateOverride reverts to lastSyncedRate ──────────────────
    [SkippableFact]
    public async Task Ln_resync_skips_pinned_rate_and_reset_restores_synced_rate()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var code = $"TAXSYNC-{tag}";
        var inbound = await CreateTaxScopedInboundClientAsync(tag);
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        // Push #1 — insert @18: TaxRate 18, LastSyncedRate 18, not overridden.
        (await PushTaxAsync(inbound, code, 18m, $"j1-{tag}")).Failed.Should().Be(0);
        var tax = await LoadTaxAsync(code);
        tax.TaxRate.Should().Be(18m);
        tax.LastSyncedRate.Should().Be(18m);
        tax.IsRateOverridden.Should().BeFalse();

        // (k) Admin edits the RATE (18 → 25) ⇒ the row is pinned.
        var pinResp = await admin.PutAsJsonAsync($"/api/masters/taxes/{tax.Id}",
            new UpdateTaxRequest(tax.Description, 25m, IsActive: true));
        pinResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(pinResp));
        var pinned = (await Read<TaxDto>(pinResp)).Data!;
        pinned.TaxRate.Should().Be(25m);
        pinned.IsRateOverridden.Should().BeTrue(because: "a TaxRate VALUE change pins the rate against the sync");

        // (j) Push #2 — LN re-syncs @20: the pinned rate WINS (stays 25); lastSyncedRate still tracks 20.
        (await PushTaxAsync(inbound, code, 20m, $"j2-{tag}")).Failed.Should().Be(0);
        var afterSync = await LoadTaxAsync(code);
        afterSync.TaxRate.Should().Be(25m, because: "the sync skips taxRate on an overridden row");
        afterSync.LastSyncedRate.Should().Be(20m, because: "lastSyncedRate ALWAYS tracks the latest inbound value");
        afterSync.IsRateOverridden.Should().BeTrue();

        // (k) ResetRateOverride ⇒ pin cleared, rate snaps back to the last synced value (20).
        var resetResp = await admin.PutAsJsonAsync($"/api/masters/taxes/{tax.Id}",
            new UpdateTaxRequest(tax.Description, 25m, IsActive: true, ResetRateOverride: true));
        resetResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resetResp));
        var reset = (await Read<TaxDto>(resetResp)).Data!;
        reset.IsRateOverridden.Should().BeFalse();
        reset.TaxRate.Should().Be(20m, because: "reset restores TaxRate = LastSyncedRate");

        // A subsequent sync owns the rate again.
        (await PushTaxAsync(inbound, code, 22m, $"j3-{tag}")).Failed.Should().Be(0);
        var afterReset = await LoadTaxAsync(code);
        afterReset.TaxRate.Should().Be(22m);
        afterReset.LastSyncedRate.Should().Be(22m);
    }

    // ── An unchanged admin save (same rate) must NOT pin the row ───────────────────────────────────────
    [SkippableFact]
    public async Task Admin_save_without_rate_change_does_not_pin()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var code = $"TAXNOP-{tag}";
        var taxId = await _fx.CreateTaxAsync(code, 18m);
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        var resp = await admin.PutAsJsonAsync($"/api/masters/taxes/{taxId}",
            new UpdateTaxRequest("Description only edit", 18m, IsActive: true));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        (await Read<TaxDto>(resp)).Data!.IsRateOverridden.Should().BeFalse(
            because: "only a rate VALUE change pins the row");
    }

    // -------------------- helpers --------------------

    /// <summary>Seeds the "Tax" inbound endpoint map + a tagged API key carrying Integration.Inbound.Tax.</summary>
    private async Task<HttpClient> CreateTaxScopedInboundClientAsync(string tag)
    {
        var plaintext = $"mok_{tag}taxsyncsecretkey0000000000";
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            if (!await db.InforEndpointMaps.IgnoreQueryFilters().AnyAsync(m =>
                    !m.IsDeleted && m.TenantId == IntegrationTestFixture.TenantId
                    && m.EntityName == "Tax" && m.Direction == SyncDirection.Inbound))
            {
                db.InforEndpointMaps.Add(new InforEndpointMap
                {
                    Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId, EntityName = "Tax",
                    Direction = SyncDirection.Inbound, InforEndpointUrl = "/api/integration/inbound/taxes",
                    BodName = "SyncTax", IsEnabled = true, ReceivedCount = 0, CreatedBy = "seed", CreatedOn = now,
                });
            }

            var keyId = Guid.NewGuid();
            db.ApiKeys.Add(new ApiKey
            {
                Id = keyId,
                TenantId = IntegrationTestFixture.TenantId,
                Label = $"Tax-sync test key {tag}",
                KeyPrefix = plaintext[..IntegrationTestFixture.ApiKeyPrefixLength],
                KeyHash = ApiKeyHasher.Hash(plaintext),
                Scopes = "Integration.Inbound.Tax",
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

    private async Task<UpsertResultDto> PushTaxAsync(HttpClient inbound, string code, decimal? rate, string idempotencyKey)
    {
        var body = new PushTaxesRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new TaxRecord(code, $"Sync tax {code}", rate, IsActive: true),
        });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/integration/inbound/taxes")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        var resp = await inbound.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<UpsertResultDto>(resp)).Data!;
    }

    private async Task<Domain.Entities.Proc.Tax> LoadTaxAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Taxes.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.TenantEntityId == IntegrationTestFixture.CompanyId && t.Code == code && !t.IsDeleted);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
