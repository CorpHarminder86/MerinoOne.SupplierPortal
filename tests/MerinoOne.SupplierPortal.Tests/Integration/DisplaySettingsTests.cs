using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Application.SystemSettings.Display;
using MerinoOne.SupplierPortal.Contracts.Common;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using MerinoOne.SupplierPortal.Infrastructure;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Inbound;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Services;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Display settings (<c>Display.MoneyDecimalPlaces</c>) end to end against the real host and the dedicated test DB:
/// <list type="bullet">
///   <item><c>GET api/system-settings/display</c> is open to any authenticated user (a supplier's pages format money
///         too) and to nobody else, while the generic settings list stays admin-only;</item>
///   <item>an admin Save / Reset reaches the endpoint AND re-points the process-wide <see cref="AppMoney"/> (the
///         host runs in-process, so the static is observable here);</item>
///   <item>malformed or out-of-range values are rejected with 400 and change nothing;</item>
///   <item>host startup seeds <see cref="AppMoney"/> from the saved value, and a failed read can never stop the
///         host booting (two extra hosts are built for that, with the background workers switched off so they
///         do not become second dispatchers on the shared test DB);</item>
///   <item>the cached reader retries after a failed load, caches a good one, and is not overwritten by a load that a
///         Save overtook.</item>
/// </list>
///
/// <para><b>The test DB persists between runs</b>, so every test that writes <c>Display.MoneyDecimalPlaces</c>
/// captures the row first and puts it back in a <c>finally</c> — including DELETING a row that did not exist, with a
/// hard delete (the <c>(category, settingKey)</c> unique index counts soft-deleted rows, so a tombstone would break
/// the next run's first Save). It also restores <see cref="AppMoney.Decimals"/>, which the Save hook moved.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DisplaySettingsTests
{
    private const string Endpoint = "/api/system-settings/display";
    private const string SettingsEndpoint = "/api/system-settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public DisplaySettingsTests(IntegrationTestFixture fx) => _fx = fx;

    // ── Authorization ───────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Anonymous_callers_are_rejected()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var response = await _fx.Factory.CreateClient().GetAsync(Endpoint);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableTheory]
    [InlineData(SecurityTestHarness.Users.Supplier)]
    [InlineData(SecurityTestHarness.Users.Buyer)]
    public async Task Any_authenticated_non_admin_user_can_read_the_money_decimal_places(string userCode)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = await _fx.ClientAsAsync(userCode, IntegrationTestFixture.CompanyId);

        // The precondition that gives the assertion its meaning: this user is NOT a settings admin — the generic
        // list refuses them — yet the display endpoint serves them.
        var generic = await client.GetAsync($"{SettingsEndpoint}?category={DisplayKeys.Category}");
        generic.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: $"{userCode} holds no Settings.Read");

        var response = await client.GetAsync(Endpoint);
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: await response.Content.ReadAsStringAsync());
        var body = await Read<DisplaySettingsDto>(response);
        body.Success.Should().BeTrue();
        body.Data!.MoneyDecimalPlaces.Should().BeInRange(AppMoney.MinDecimals, AppMoney.MaxDecimals);
    }

    // ── Save / Reset ────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Admin_save_and_reset_reach_the_endpoint_and_the_process_wide_formatter()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var original = await CaptureAsync(admin);

        // A value DIFFERENT from what is there now, or the round trip would prove nothing. Both candidates also
        // differ from the Reset value (4), so each step below is observable.
        var target = original.Effective == 2 ? 3 : 2;

        try
        {
            var save = await SaveAsync(admin, target.ToString());
            save.StatusCode.Should().Be(HttpStatusCode.OK, because: await save.Content.ReadAsStringAsync());
            (await GetPlacesAsync(admin)).Should().Be(target);
            AppMoney.Decimals.Should().Be(target, because: "the Save handler re-points the process-wide formatter after it commits");

            var reset = await admin.PostAsJsonAsync($"{SettingsEndpoint}/reset",
                new ResetSystemSettingRequest(DisplayKeys.Category, DisplayKeys.MoneyDecimalPlaces));
            reset.StatusCode.Should().Be(HttpStatusCode.OK, because: await reset.Content.ReadAsStringAsync());
            (await GetPlacesAsync(admin)).Should().Be(AppMoney.DefaultDecimals);
            AppMoney.Decimals.Should().Be(AppMoney.DefaultDecimals, because: "Reset goes through the same hook");
        }
        finally
        {
            await RestoreAsync(original);
        }

        (await GetPlacesAsync(admin)).Should().Be(original.Effective, because: "the test must leave the shared DB as it found it");
        AppMoney.Decimals.Should().Be(original.AppMoneyDecimals);
    }

    [SkippableTheory]
    [InlineData("7")]      // above the range
    [InlineData("-1")]     // below it
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("2.5")]    // not an integer
    [InlineData(" 3")]     // digits only — no sign, no whitespace
    public async Task Malformed_or_out_of_range_values_are_rejected_and_change_nothing(string value)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var original = await CaptureAsync(admin);

        try
        {
            var response = await SaveAsync(admin, value);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
            (await GetPlacesAsync(admin)).Should().Be(original.Effective);
            AppMoney.Decimals.Should().Be(original.AppMoneyDecimals);
        }
        finally
        {
            await RestoreAsync(original);
        }
    }

    // ── Startup ─────────────────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Startup_seeds_AppMoney_from_the_saved_setting_and_the_workers_switch_applies_to_a_real_host()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var original = await CaptureAsync(admin);
        var saved = original.Effective == 2 ? 3 : 2;

        try
        {
            (await SaveAsync(admin, saved.ToString())).StatusCode.Should().Be(HttpStatusCode.OK);

            // The Save hook just moved the static; put it back, so the only thing that can make it `saved` again
            // is the next host's own startup code.
            AppMoney.Configure(AppMoney.DefaultDecimals);

            await using var second = _fx.Factory.WithWebHostBuilder(b => b.UseSetting("BackgroundWorkers:Enabled", "false"));
            _ = second.Services;        // boots the host; Program.cs's startup code has run by the time it is serving

            AppMoney.Decimals.Should().Be(saved, because: "startup reads the saved Display setting into the formatter");
            second.Services.GetServices<IHostedService>()
                .Select(service => service.GetType())
                .Where(type => type.Namespace?.StartsWith("MerinoOne.SupplierPortal", StringComparison.Ordinal) == true)
                .Should().BeEmpty(because: "BackgroundWorkers:Enabled=false removes every portal worker from a real host too");
        }
        finally
        {
            await RestoreAsync(original);
        }
    }

    [SkippableFact]
    public async Task A_failure_reading_the_display_settings_never_stops_the_host_booting()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var before = AppMoney.Decimals;
        AppMoney.Configure(5);          // distinct from the default (4), so "left alone" is observable

        try
        {
            await using var second = _fx.Factory.WithWebHostBuilder(b => b
                .UseSetting("BackgroundWorkers:Enabled", "false")
                .ConfigureTestServices(services => services.AddSingleton<IDisplaySettings, ThrowingDisplaySettings>()));

            var boot = () => second.Services;
            boot.Should().NotThrow(because: "a display preference must never be able to take the host down");
            AppMoney.Decimals.Should().Be(5, because: "a failed read leaves the formatter as it was");
        }
        finally
        {
            AppMoney.Configure(before);
        }
    }

    // ── The cached reader ───────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_failed_load_returns_the_default_but_is_not_cached()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var probe = new ProbeScopeFactory(_fx.Factory.Services.GetRequiredService<IServiceScopeFactory>()) { FailNext = true };
        var service = NewService(probe);

        (await service.GetMoneyDecimalPlacesAsync(CancellationToken.None)).Should().Be(AppMoney.DefaultDecimals,
            because: "an unreadable settings table must degrade to the default, never throw");
        probe.Created.Should().Be(1);

        var recovered = await service.GetMoneyDecimalPlacesAsync(CancellationToken.None);
        probe.Created.Should().Be(2, because: "the failure must not pin the default — the next call retries");
        recovered.Should().BeInRange(AppMoney.MinDecimals, AppMoney.MaxDecimals);
    }

    [SkippableFact]
    public async Task A_good_load_is_cached_until_the_Display_category_is_invalidated()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var probe = new ProbeScopeFactory(_fx.Factory.Services.GetRequiredService<IServiceScopeFactory>());
        var service = NewService(probe);

        await service.GetMoneyDecimalPlacesAsync(CancellationToken.None);
        await service.GetMoneyDecimalPlacesAsync(CancellationToken.None);
        probe.Created.Should().Be(1, because: "the second read is served from the snapshot");

        service.InvalidateCategory("Fulfilment");
        await service.GetMoneyDecimalPlacesAsync(CancellationToken.None);
        probe.Created.Should().Be(1, because: "another category's Save must not drop this snapshot");

        service.InvalidateCategory(DisplayKeys.Category);
        await service.GetMoneyDecimalPlacesAsync(CancellationToken.None);
        probe.Created.Should().Be(2, because: "a Save/Reset of Display drops the snapshot so the next read sees it");
    }

    [SkippableFact]
    public async Task A_load_that_a_save_overtook_is_not_cached()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var probe = new ProbeScopeFactory(_fx.Factory.Services.GetRequiredService<IServiceScopeFactory>())
        {
            Entered = entered,
            Release = release,
        };
        var service = NewService(probe);

        // A read starts loading and parks inside its scope creation — i.e. after it has noted the snapshot
        // generation but before it can publish a snapshot.
        var inFlight = Task.Run(() => service.GetMoneyDecimalPlacesAsync(CancellationToken.None));
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the load should be in flight");

        // A Save lands and invalidates while that read is still in flight, then the read is let finish.
        service.InvalidateCategory(DisplayKeys.Category);
        release.Set();
        await inFlight;

        probe.Entered = null;
        probe.Release = null;
        await service.GetMoneyDecimalPlacesAsync(CancellationToken.None);
        probe.Created.Should().Be(2, because: "the overtaken load may have read the pre-Save value, so it must not become the snapshot");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static DisplaySettingsService NewService(IServiceScopeFactory scopes)
        => new(scopes, new DisplaySeed(), NullLogger<DisplaySettingsService>.Instance);

    private static Task<HttpResponseMessage> SaveAsync(HttpClient admin, string value)
        => admin.PostAsJsonAsync(SettingsEndpoint,
            new SaveSystemSettingRequest(DisplayKeys.Category, DisplayKeys.MoneyDecimalPlaces, value));

    private static async Task<int> GetPlacesAsync(HttpClient client)
    {
        var response = await client.GetAsync(Endpoint);
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: await response.Content.ReadAsStringAsync());
        return (await Read<DisplaySettingsDto>(response)).Data!.MoneyDecimalPlaces;
    }

    /// <summary>What a test must put back: the stored row (null ⇒ there was none), what callers saw, and the static.</summary>
    private sealed record Original(string? StoredValue, int Effective, int AppMoneyDecimals);

    private async Task<Original> CaptureAsync(HttpClient client)
    {
        string? stored;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            stored = await db.SystemSettings.IgnoreQueryFilters()
                .Where(s => s.Category == DisplayKeys.Category && s.SettingKey == DisplayKeys.MoneyDecimalPlaces)
                .Select(s => s.SettingValue)
                .SingleOrDefaultAsync();
        }

        return new Original(stored, await GetPlacesAsync(client), AppMoney.Decimals);
    }

    private async Task RestoreAsync(Original original)
    {
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = db.SystemSettings.IgnoreQueryFilters()
                .Where(s => s.Category == DisplayKeys.Category && s.SettingKey == DisplayKeys.MoneyDecimalPlaces);

            if (original.StoredValue is null)
                await rows.ExecuteDeleteAsync();                                   // hard delete — see the class remarks
            else
                await rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.SettingValue, original.StoredValue));
        }

        // Direct SQL bypassed the Save/Reset handlers, so do their two jobs by hand.
        foreach (var invalidator in _fx.Factory.Services.GetServices<ISettingsCacheInvalidator>())
            invalidator.InvalidateCategory(DisplayKeys.Category);
        AppMoney.Configure(original.AppMoneyDecimals);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    /// <summary>A reader that always fails — what startup sees when there is no user context or the DB is down.</summary>
    private sealed class ThrowingDisplaySettings : IDisplaySettings
    {
        public Task<int> GetMoneyDecimalPlacesAsync(CancellationToken ct)
            => throw new InvalidOperationException("simulated: display settings unreadable");
    }

    /// <summary>
    /// Wraps the real scope factory to count loads, fail the next one, or park a load mid-flight — which is how the
    /// reader's retry / cache / overtaken-load behaviour is observed without sleeping.
    /// </summary>
    private sealed class ProbeScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int _created;
        public int Created => Volatile.Read(ref _created);
        public bool FailNext { get; set; }
        public ManualResetEventSlim? Entered { get; set; }
        public ManualResetEventSlim? Release { get; set; }

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _created);

            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("simulated settings outage");
            }

            Entered?.Set();
            Release?.Wait(TimeSpan.FromSeconds(10));
            return inner.CreateScope();
        }
    }
}

/// <summary>
/// The Display seed's contract, with no host and no DB: the category and key names (they are stored in the settings
/// table, so renaming either orphans the saved value), the default, and the validator — which must accept exactly
/// what the reader will honour.
/// </summary>
public class DisplaySeedTests
{
    private static readonly DisplaySeed Seed = new();

    [Fact]
    public void Declares_the_Display_category_defaulting_to_four_places()
    {
        Seed.Category.Should().Be("Display");
        DisplayKeys.Category.Should().Be("Display");
        DisplayKeys.MoneyDecimalPlaces.Should().Be("MoneyDecimalPlaces");

        Seed.Defaults[DisplayKeys.MoneyDecimalPlaces].Should().Be("4");
        Seed.Validate(DisplayKeys.MoneyDecimalPlaces, Seed.Defaults[DisplayKeys.MoneyDecimalPlaces])
            .Should().BeNull(because: "the default must itself be a legal value");
        Seed.Descriptions[DisplayKeys.MoneyDecimalPlaces].Should().NotBeNullOrWhiteSpace()
            .And.Subject!.Length.Should().BeLessThanOrEqualTo(500, because: "systemSetting.description is nvarchar(500)");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    public void Accepts_every_precision_from_zero_to_six(string value)
        => Seed.Validate(DisplayKeys.MoneyDecimalPlaces, value).Should().BeNull();

    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("+3")]
    [InlineData(" 3")]
    [InlineData("3 ")]
    [InlineData("2.5")]
    [InlineData("1e1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("99999999999")]    // overflows int
    public void Rejects_anything_that_is_not_a_plain_integer_in_range(string value)
        => Seed.Validate(DisplayKeys.MoneyDecimalPlaces, value).Should().Contain("0..6");

    [Fact]
    public void Rejects_an_unknown_key()
        => Seed.Validate("NoSuchKey", "1").Should().Contain("NoSuchKey");
}

/// <summary>
/// <c>BackgroundWorkers:Enabled</c> — the one switch that keeps a local run against the shared dev DB from becoming a
/// second dispatcher. Asserted on the service registrations, so it needs neither a host nor a DB. The disabled case
/// is the guard that matters: a worker added later with a bare <c>AddHostedService</c> would sit outside the gate and
/// fail it.
/// </summary>
public class BackgroundWorkersGateTests
{
    private static readonly Type[] PortalWorkers =
    [
        typeof(OutboxDispatcherWorker),
        typeof(IdmDocumentOutboxWorker),
        typeof(LnGateReconciliationWorker),
        typeof(HeldInboundReplayWorker),
        typeof(EmailOutboxWorker),
        typeof(SlaNudgeWorker),
    ];

    /// <summary>The portal's own hosted services (the framework registers a few of its own, which are not ours to gate).</summary>
    private static List<Type> PortalHostedServices(string? enabled)
    {
        var settings = new Dictionary<string, string?>
        {
            // AddInfrastructure refuses a blank connection string; nothing in this test ever opens it.
            ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=unused;Trusted_Connection=True;",
        };
        if (enabled is not null) settings["BackgroundWorkers:Enabled"] = enabled;

        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        return services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .OfType<Type>()
            .Where(t => t.Namespace?.StartsWith("MerinoOne.SupplierPortal", StringComparison.Ordinal) == true)
            .ToList();
    }

    [Fact]
    public void Disabled_registers_none_of_the_portals_workers()
        => PortalHostedServices("false").Should().BeEmpty(
            because: "a worker registered outside the gate would still drain the shared outbox from a local run");

    [Theory]
    [InlineData(null)]       // key absent — every deployed host and the test host
    [InlineData("true")]
    public void Unset_or_true_registers_every_worker(string? enabled)
        => PortalHostedServices(enabled).Should().Contain(PortalWorkers);
}
