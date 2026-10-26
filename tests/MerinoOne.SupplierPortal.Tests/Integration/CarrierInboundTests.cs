using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Controllers;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R17 — <c>POST /api/integration/inbound/carriers</c> (X-APIKey, <c>Integration.Inbound.Carrier</c>), exercised through the REAL host
/// against the dedicated SQL test DB. A clone of the delivery-term push: company-shared upsert keyed on (source company, Code)
/// case-insensitively; Code is required and at most 9, Description is OPTIONAL and at most 50; only Description/IsActive are updated.
///
/// <para>The test DB is persistent, so every test mints its own carrier codes (<c>I</c> + six hex digits = 7 characters, unique per
/// run) and removes whatever it created in a <c>finally</c> (bulk DELETE: no change tracker, no soft-delete interceptor). No shared
/// or seeded row is read for its content or mutated.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class CarrierInboundTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Url = "/api/integration/inbound/carriers";

    private readonly IntegrationTestFixture _fx;
    public CarrierInboundTests(IntegrationTestFixture fx) => _fx = fx;

    // ── a new code is inserted under the resolved company ──────────────────────────────────────────────
    [SkippableFact]
    public async Task Pushing_a_new_carrier_inserts_it_under_the_resolved_company()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        try
        {
            var result = await PushAsync(_fx.CreateInboundClient(), new CarrierRecord(code, "DHL Express", true));

            result.Received.Should().Be(1);
            (result.Inserted, result.Updated, result.Skipped, result.Failed).Should().Be((1, 0, 0, 0));
            result.Rows.Should().ContainSingle().Which.Should().Match<RowResult>(r => r.Code == code && r.Outcome == RowOutcome.Inserted);

            var row = await LoadAsync(code);
            row.Should().NotBeNull();
            row!.Description.Should().Be("DHL Express");
            row.IsActive.Should().BeTrue();
            row.TenantId.Should().Be(IntegrationTestFixture.TenantId);
            row.TenantEntityId.Should().Be(IntegrationTestFixture.CompanyId, "the row lands under the resolved source company");
            row.CreatedBy.Should().Be("infor:inbound");
        }
        finally { await DeleteAsync(code); }
    }

    // ── re-pushing a changed description updates the existing row ──────────────────────────────────────
    [SkippableFact]
    public async Task Re_pushing_a_changed_description_updates_the_existing_row()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var client = _fx.CreateInboundClient();
        try
        {
            (await PushAsync(client, new CarrierRecord(code, "Blue Dart")))
                .Inserted.Should().Be(1);

            var second = await PushAsync(client, new CarrierRecord(code, "Blue Dart Express", IsActive: false));

            (second.Inserted, second.Updated, second.Skipped, second.Failed).Should().Be((0, 1, 0, 0));
            second.Rows.Should().ContainSingle().Which.Outcome.Should().Be(RowOutcome.Updated);

            var row = await LoadAsync(code);
            row!.Description.Should().Be("Blue Dart Express");
            row.IsActive.Should().BeFalse("isActive is ERP-fed on an existing row too");
            row.UpdatedBy.Should().NotBeNullOrEmpty("the audit block is stamped on an update (the audit interceptor records the API-key principal)");
            row.UpdatedOn.Should().NotBeNull();
        }
        finally { await DeleteAsync(code); }
    }

    // ── an identical re-push is Skipped ─────────────────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Re_pushing_an_identical_carrier_is_skipped()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var client = _fx.CreateInboundClient();
        try
        {
            var record = new CarrierRecord(code, "FedEx");
            (await PushAsync(client, record)).Inserted.Should().Be(1);
            var before = await LoadAsync(code);

            // (1) The identical body with no Idempotency-Key is a payload-hash replay: the executor short-circuits and reports Skipped.
            var replay = await PushAsync(client, record);
            (replay.Inserted, replay.Updated, replay.Skipped, replay.Failed).Should().Be((0, 0, 1, 0));

            // (2) The same row in a fresh batch (explicit new key, so it is NOT a replay) is judged row-by-row: nothing differs → Skipped,
            //     and the stored row is not touched (no UpdatedOn churn).
            var fresh = await PushAsync(client, record, idempotencyKey: $"carrier-skip-{Guid.NewGuid():N}");
            (fresh.Inserted, fresh.Updated, fresh.Skipped, fresh.Failed).Should().Be((0, 0, 1, 0));
            fresh.Rows.Should().ContainSingle().Which.Outcome.Should().Be(RowOutcome.Skipped);

            var after = await LoadAsync(code);
            after!.UpdatedOn.Should().Be(before!.UpdatedOn, "an unchanged row is not rewritten");
            after.Description.Should().Be("FedEx");
        }
        finally { await DeleteAsync(code); }
    }

    // ── description is optional: omitted → stored null; the key matches case-insensitively ─────────────
    [SkippableFact]
    public async Task A_carrier_without_a_description_is_stored_with_null_and_the_code_matches_case_insensitively()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();                       // upper-case hex
        var client = _fx.CreateInboundClient();
        try
        {
            // The body carries no "description" property at all (LN can send a carrier code with no name).
            var resp = await client.PostAsJsonAsync(Url, new { companyCode = IntegrationTestFixture.CompanyCode, carriers = new[] { new { code } } });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
            (await Read(resp)).Inserted.Should().Be(1);
            var row = await LoadAsync(code);
            row!.Description.Should().BeNull();
            row.IsActive.Should().BeTrue("isActive defaults to true");

            // The same code in lower case addresses the SAME row (update, not a second insert).
            var again = await PushAsync(client, new CarrierRecord(code.ToLowerInvariant(), "Gati Ltd"));
            (again.Inserted, again.Updated).Should().Be((0, 1));
            (await CountAsync(code)).Should().Be(1, "the key is case-insensitive");
            (await LoadAsync(code))!.Description.Should().Be("Gati Ltd");
        }
        finally { await DeleteAsync(code); }
    }

    // ── validation: code is at most 9 → 400, nothing written ───────────────────────────────────────────
    [SkippableFact]
    public async Task A_carrier_code_of_ten_characters_is_rejected_with_400_and_writes_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode() + "ABC";                // 7 + 3 = 10 characters
        code.Length.Should().Be(10);
        try
        {
            var resp = await _fx.CreateInboundClient().PostAsJsonAsync(Url,
                new PushCarriersRequest(IntegrationTestFixture.CompanyCode, new[] { new CarrierRecord(code, "Too long") }));

            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await resp.Content.ReadAsStringAsync());
            (await CountAsync(code)).Should().Be(0);
        }
        finally { await DeleteAsync(code); }
    }

    [SkippableFact]
    public async Task A_nine_character_code_is_accepted_and_a_description_over_fifty_or_a_blank_code_or_a_duplicate_code_is_400()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var nine = NewCode() + "XY";                 // 7 + 2 = 9 characters — exactly the cap
        nine.Length.Should().Be(9);
        var dup = NewCode();
        var client = _fx.CreateInboundClient();
        try
        {
            (await PushAsync(client, new CarrierRecord(nine, new string('d', 50)))).Inserted.Should().Be(1);

            async Task AssertBadRequest(string because, params CarrierRecord[] records)
            {
                var resp = await client.PostAsJsonAsync(Url, new PushCarriersRequest(IntegrationTestFixture.CompanyCode, records));
                resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: because + " — " + await resp.Content.ReadAsStringAsync());
            }

            await AssertBadRequest("a 51-character description exceeds the cap", new CarrierRecord(NewCode(), new string('d', 51)));
            await AssertBadRequest("a blank code is invalid", new CarrierRecord("  ", "Blank"));
            await AssertBadRequest("a duplicate code (any case) in one batch is invalid",
                new CarrierRecord(dup, "One"), new CarrierRecord(dup.ToLowerInvariant(), "Two"));
            (await CountAsync(dup)).Should().Be(0, "a rejected batch writes nothing");
        }
        finally { await DeleteAsync(nine); await DeleteAsync(dup); }
    }

    // ── scope gate: a key without Integration.Inbound.Carrier is refused at the policy ─────────────────
    [SkippableFact]
    public async Task Push_with_a_key_lacking_the_carrier_scope_is_403_and_writes_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        try
        {
            var client = await _fx.CreateScopedInboundClientAsync("Integration.Inbound.Grn");
            var resp = await client.PostAsJsonAsync(Url,
                new PushCarriersRequest(IntegrationTestFixture.CompanyCode, new[] { new CarrierRecord(code, "Nope") }));

            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                because: "the key lacks Integration.Inbound.Carrier, so the [Authorize] policy denies the request");
            (await CountAsync(code)).Should().Be(0, "the handler never ran");
        }
        finally { await DeleteAsync(code); }
    }

    // ── docs: the developer-docs catalog sample is a valid request body (the second docs source beside [EndpointDescription]) ──
    [Fact]
    public void The_catalog_sample_for_carriers_deserializes_into_the_request_contract()
    {
        var entry = IntegrationCatalog.All.Single(e => e.Scope == "Integration.Inbound.Carrier");
        entry.Path.Should().Be("api/integration/inbound/carriers");

        var body = JsonSerializer.Deserialize<PushCarriersRequest>(entry.SampleJson, Json)!;
        body.CompanyCode.Should().Be("2000");
        body.Carriers.Should().ContainSingle().Which.Should().Be(new CarrierRecord("DHL", "DHL Express", true));
    }

    // -------------------- helpers --------------------

    /// <summary><c>I</c> + six hex digits (upper case) — 7 characters, comfortably inside the 9-character cap, unique per call.</summary>
    private static string NewCode() => "I" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<UpsertResultDto> PushAsync(HttpClient client, CarrierRecord record, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = JsonContent.Create(new PushCarriersRequest(IntegrationTestFixture.CompanyCode, new[] { record })),
        };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
        return await Read(resp);
    }

    private static async Task<UpsertResultDto> Read(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<UpsertResultDto>>(stream, Json))!.Data!;
    }

    private async Task<Carrier?> LoadAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Carriers.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantEntityId == IntegrationTestFixture.CompanyId && c.Code == code && !c.IsDeleted);
    }

    private async Task<int> CountAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Carriers.IgnoreQueryFilters()
            .CountAsync(c => c.TenantEntityId == IntegrationTestFixture.CompanyId && c.Code == code && !c.IsDeleted);
    }

    private async Task DeleteAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Carriers.IgnoreQueryFilters()
            .Where(c => c.TenantEntityId == IntegrationTestFixture.CompanyId && c.Code == code)
            .ExecuteDeleteAsync();
    }
}
