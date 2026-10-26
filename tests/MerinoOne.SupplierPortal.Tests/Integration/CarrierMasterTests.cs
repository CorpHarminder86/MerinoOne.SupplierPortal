using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R17 — Carrier master admin API (<c>/api/masters/carriers</c>), cloned from the delivery-term master: company-scoped and
/// share-group aware, Code required / trimmed / at most 9 and immutable, Description OPTIONAL and at most 50, a duplicate code in
/// the company is a 409. Reads need only an authenticated user (the supplier's ASN Carrier dropdown); writes need Settings.Write.
///
/// <para>The test DB is persistent, so every test mints its own carrier codes (<c>C</c> + six hex digits = 7 characters, unique
/// per run) and removes whatever it created in a <c>finally</c> (bulk DELETE: no tracker, no soft-delete interceptor). No shared
/// or seeded row is read for its content or mutated.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class CarrierMasterTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public CarrierMasterTests(IntegrationTestFixture fx) => _fx = fx;

    // ── create → GET by id returns it; the row lands in the admin's active company ───────────────────
    [SkippableFact]
    public async Task Created_carrier_is_returned_by_id_and_lands_in_the_admins_active_company()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var admin = await AdminAsync();
        try
        {
            var created = await CreateAsync(admin, new CreateCarrierRequest(code, "Blue Dart Express"));
            created.Code.Should().Be(code);
            created.Description.Should().Be("Blue Dart Express");
            created.IsActive.Should().BeTrue("IsActive defaults to true");

            var get = await admin.GetAsync($"/api/masters/carriers/{created.Id}");
            get.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(get));
            var fetched = (await Read<CarrierDto>(get)).Data!;
            (fetched.Id, fetched.Code, fetched.Description, fetched.IsActive).Should().Be((created.Id, code, "Blue Dart Express", true));

            // The ScopeStampInterceptor stamped tenant + company from the request context, and the handler stamped the audit user.
            var row = await LoadRowAsync(code);
            row.TenantEntityId.Should().Be(IntegrationTestFixture.CompanyId, "the company is stamped on add from the admin's active company");
            row.TenantId.Should().Be(IntegrationTestFixture.TenantId);
            row.CreatedBy.Should().Be(SecurityTestHarness.Users.Admin);
            row.UpdatedBy.Should().BeNull();
        }
        finally { await DeleteAsync(code); }
    }

    // ── description is optional: omitted → created, null ─────────────────────────────────────────────
    [SkippableFact]
    public async Task Carrier_can_be_created_without_a_description_and_reads_back_with_null()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var admin = await AdminAsync();
        try
        {
            // The body carries no "description" property at all (LN can send a carrier code with no name).
            var resp = await admin.PostAsJsonAsync("/api/masters/carriers", new { code });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
            var created = (await Read<CarrierDto>(resp)).Data!;
            created.Description.Should().BeNull();
            created.IsActive.Should().BeTrue();

            var get = await admin.GetAsync($"/api/masters/carriers/{created.Id}");
            (await Read<CarrierDto>(get)).Data!.Description.Should().BeNull();
            (await LoadRowAsync(code)).Description.Should().BeNull("a missing description is stored as NULL, not an empty string");
        }
        finally { await DeleteAsync(code); }
    }

    // ── the code is trimmed before it is checked, stored and compared ───────────────────────────────
    [SkippableFact]
    public async Task Code_is_trimmed_and_a_padded_duplicate_is_still_a_conflict()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var admin = await AdminAsync();
        try
        {
            var created = await CreateAsync(admin, new CreateCarrierRequest($"  {code}  ", "  Padded description  "));
            created.Code.Should().Be(code, "the code is stored trimmed");
            created.Description.Should().Be("Padded description");

            var again = await admin.PostAsJsonAsync("/api/masters/carriers", new CreateCarrierRequest($" {code}", null));
            again.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await Body(again));
        }
        finally { await DeleteAsync(code); }
    }

    // ── validation: code too long / blank, description too long ──────────────────────────────────────
    [SkippableFact]
    public async Task Code_of_ten_characters_is_rejected_with_400_and_nothing_is_created()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var admin = await AdminAsync();
        var tooLong = "T" + Guid.NewGuid().ToString("N")[..9];   // 10 characters
        tooLong.Length.Should().Be(10);

        var resp = await admin.PostAsJsonAsync("/api/masters/carriers", new CreateCarrierRequest(tooLong, "Too long"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
        (await Read<CarrierDto>(resp)).Errors.Should().Contain(e => e.Contains("Code", StringComparison.Ordinal));

        (await ListAsync(admin, isActive: null)).Should().NotContain(c => c.Code == tooLong);
    }

    [SkippableFact]
    public async Task Code_of_exactly_nine_characters_is_accepted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = "N" + Guid.NewGuid().ToString("N")[..8];   // 9 characters: the column width
        code.Length.Should().Be(9);
        var admin = await AdminAsync();
        try
        {
            (await CreateAsync(admin, new CreateCarrierRequest(code, "Nine wide"))).Code.Should().Be(code);
        }
        finally { await DeleteAsync(code); }
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_code_is_rejected_with_400(string blank)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var admin = await AdminAsync();
        var resp = await admin.PostAsJsonAsync("/api/masters/carriers", new CreateCarrierRequest(blank, "No code"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(resp));
    }

    [SkippableFact]
    public async Task Description_of_fifty_one_characters_is_rejected_on_create_and_on_update_but_fifty_is_accepted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var rejected = NewCode();
        var admin = await AdminAsync();
        try
        {
            var tooLong = new string('d', 51);
            var exact = new string('d', 50);

            var create = await admin.PostAsJsonAsync("/api/masters/carriers", new CreateCarrierRequest(rejected, tooLong));
            create.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(create));
            (await ListAsync(admin, isActive: null)).Should().NotContain(c => c.Code == rejected, "a rejected create stores nothing");

            var created = await CreateAsync(admin, new CreateCarrierRequest(code, exact));
            created.Description.Should().Be(exact, "50 characters is the column width");

            var update = await admin.PutAsJsonAsync($"/api/masters/carriers/{created.Id}", new UpdateCarrierRequest(tooLong, true));
            update.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await Body(update));
            (await LoadRowAsync(code)).Description.Should().Be(exact, "a rejected update leaves the row untouched");
        }
        finally { await DeleteAsync(code, rejected); }
    }

    // ── duplicate code in the same company → 409 ─────────────────────────────────────────────────────
    [SkippableFact]
    public async Task Duplicate_code_in_the_same_company_is_a_409()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var admin = await AdminAsync();
        try
        {
            await CreateAsync(admin, new CreateCarrierRequest(code, "First"));

            var dup = await admin.PostAsJsonAsync("/api/masters/carriers", new CreateCarrierRequest(code, "Second"));
            dup.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await Body(dup));
            (await Read<CarrierDto>(dup)).Errors.Should().Contain(e => e.Contains(code, StringComparison.Ordinal));

            (await ListAsync(admin, isActive: null)).Count(c => c.Code == code).Should().Be(1);
        }
        finally { await DeleteAsync(code); }
    }

    // ── update: description + IsActive change, code immutable ────────────────────────────────────────
    [SkippableFact]
    public async Task Update_changes_description_and_active_flag_but_never_the_code()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var admin = await AdminAsync();
        try
        {
            var created = await CreateAsync(admin, new CreateCarrierRequest(code, "Before"));

            var put = await admin.PutAsJsonAsync($"/api/masters/carriers/{created.Id}", new UpdateCarrierRequest("After", IsActive: false));
            put.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(put));
            var updated = (await Read<CarrierDto>(put)).Data!;
            (updated.Id, updated.Code, updated.Description, updated.IsActive).Should().Be((created.Id, code, "After", false));

            var row = await LoadRowAsync(code);
            (row.Code, row.Description, row.IsActive).Should().Be((code, "After", false));
            row.UpdatedBy.Should().Be(SecurityTestHarness.Users.Admin);
            row.UpdatedOn.Should().NotBeNull();

            // A blank description clears it (it is optional); reactivating works the same way.
            var cleared = await admin.PutAsJsonAsync($"/api/masters/carriers/{created.Id}", new UpdateCarrierRequest("   ", IsActive: true));
            cleared.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(cleared));
            var after = (await Read<CarrierDto>(cleared)).Data!;
            (after.Code, after.Description, after.IsActive).Should().Be((code, null, true));
        }
        finally { await DeleteAsync(code); }
    }

    // ── deactivate: isActive=false; the isActive filter excludes it, the unfiltered list keeps it ────
    [SkippableFact]
    public async Task Deactivated_carrier_drops_out_of_the_active_list_but_stays_in_the_full_list()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var admin = await AdminAsync();
        try
        {
            var created = await CreateAsync(admin, new CreateCarrierRequest(code, "To deactivate"));
            (await ListAsync(admin, isActive: true)).Should().Contain(c => c.Code == code);

            var deactivate = await admin.PostAsync($"/api/masters/carriers/{created.Id}/deactivate", content: null);
            deactivate.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(deactivate));
            (await Read<object>(deactivate)).Success.Should().BeTrue();

            var get = await admin.GetAsync($"/api/masters/carriers/{created.Id}");
            (await Read<CarrierDto>(get)).Data!.IsActive.Should().BeFalse();

            (await ListAsync(admin, isActive: true)).Should().NotContain(c => c.Code == code, "?isActive=true hides it");
            (await ListAsync(admin, isActive: false)).Should().Contain(c => c.Code == code, "?isActive=false shows it");
            var all = await ListAsync(admin, isActive: null);
            all.Should().Contain(c => c.Code == code, "no filter returns inactive rows too");
            all.Single(c => c.Code == code).IsActive.Should().BeFalse();

            var row = await LoadRowAsync(code);
            row.UpdatedBy.Should().Be(SecurityTestHarness.Users.Admin);
        }
        finally { await DeleteAsync(code); }
    }

    // ── the EF sentinel: creating inactive must persist inactive ─────────────────────────────────────
    [SkippableFact]
    public async Task Carrier_created_with_IsActive_false_is_persisted_inactive()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var admin = await AdminAsync();
        try
        {
            var created = await CreateAsync(admin, new CreateCarrierRequest(code, "Born inactive", IsActive: false));
            created.IsActive.Should().BeFalse();

            // Read straight from the table: with HasDefaultValue(true) alone EF would have omitted the column and stored 1.
            (await LoadRowAsync(code)).IsActive.Should().BeFalse("the sentinel makes false an ordinary value that is written");

            var get = await admin.GetAsync($"/api/masters/carriers/{created.Id}");
            (await Read<CarrierDto>(get)).Data!.IsActive.Should().BeFalse();
            (await ListAsync(admin, isActive: true)).Should().NotContain(c => c.Code == code);
        }
        finally { await DeleteAsync(code); }
    }

    // ── unknown id → 404 on get / update / deactivate ────────────────────────────────────────────────
    [SkippableFact]
    public async Task Unknown_carrier_id_is_a_404_for_get_update_and_deactivate()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var admin = await AdminAsync();
        var missing = Guid.NewGuid();

        (await admin.GetAsync($"/api/masters/carriers/{missing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync($"/api/masters/carriers/{missing}", new UpdateCarrierRequest("x", true)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsync($"/api/masters/carriers/{missing}/deactivate", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── authorization: any authenticated user reads (the ASN dropdown); only Settings.Write mutates ──
    [SkippableFact]
    public async Task Supplier_user_can_list_and_read_carriers_but_cannot_create_update_or_deactivate()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var code = NewCode();
        var blocked = NewCode();
        var admin = await AdminAsync();
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        try
        {
            var created = await CreateAsync(admin, new CreateCarrierRequest(code, "Visible to suppliers"));

            // Reads: 200 for the supplier-role user, and the admin-created row is in the company-scoped list.
            var list = await supplier.GetAsync("/api/masters/carriers?isActive=true");
            list.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(list));
            (await Read<List<CarrierDto>>(list)).Data!.Should().Contain(c => c.Code == code);

            var get = await supplier.GetAsync($"/api/masters/carriers/{created.Id}");
            get.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(get));

            // Writes: 403 (no Settings.Write), and nothing changed.
            var post = await supplier.PostAsJsonAsync("/api/masters/carriers", new CreateCarrierRequest(blocked, "Not allowed"));
            post.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: await Body(post));

            var put = await supplier.PutAsJsonAsync($"/api/masters/carriers/{created.Id}", new UpdateCarrierRequest("Hacked", false));
            put.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: await Body(put));

            var deactivate = await supplier.PostAsync($"/api/masters/carriers/{created.Id}/deactivate", content: null);
            deactivate.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: await Body(deactivate));

            var row = await LoadRowAsync(code);
            (row.Description, row.IsActive).Should().Be(("Visible to suppliers", true));
            (await ListAsync(admin, isActive: null)).Should().NotContain(c => c.Code == blocked);
        }
        finally { await DeleteAsync(code, blocked); }
    }

    [SkippableFact]
    public async Task Anonymous_caller_is_unauthorized_on_the_carrier_list()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var anonymous = _fx.Factory.CreateClient();
        (await anonymous.GetAsync("/api/masters/carriers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -------------------- helpers --------------------

    /// <summary>"C" + six hex digits: 7 characters (the column is 9 wide), unique per run.</summary>
    private static string NewCode() => "C" + Guid.NewGuid().ToString("N")[..6];

    private Task<HttpClient> AdminAsync()
        => _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

    /// <summary>Creates a carrier through the master API, failing with the response body if it is refused.</summary>
    private static async Task<CarrierDto> CreateAsync(HttpClient admin, CreateCarrierRequest request)
    {
        var resp = await admin.PostAsJsonAsync("/api/masters/carriers", request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: $"carrier '{request.Code}' must be creatable: " + await Body(resp));
        return (await Read<CarrierDto>(resp)).Data!;
    }

    private static async Task<List<CarrierDto>> ListAsync(HttpClient client, bool? isActive)
    {
        var url = isActive.HasValue ? $"/api/masters/carriers?isActive={isActive.Value.ToString().ToLowerInvariant()}" : "/api/masters/carriers";
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return (await Read<List<CarrierDto>>(resp)).Data!;
    }

    /// <summary>The live carrier row with this code under the fixture company, read past the query filters (an exact table read).</summary>
    private async Task<Carrier> LoadRowAsync(string code)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Carriers.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.TenantEntityId == IntegrationTestFixture.CompanyId && c.Code == code && !c.IsDeleted);
    }

    /// <summary>Hard-deletes this test's own carriers, one exact code at a time (bulk DELETE: no tracker, no soft-delete interceptor).</summary>
    private async Task DeleteAsync(params string[] codes)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var code in codes)
            await db.Carriers.IgnoreQueryFilters().Where(c => c.Code == code).ExecuteDeleteAsync();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
