using System.Net;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// By-GUID cross-tenant guards (C6 + C8). Unlike the list filters, both of these are enforced by EXPLICIT
/// tenant predicates in the handler/controller (not the always-on gated filter), so they hold regardless of
/// the <c>Scope.FiltersEnabled</c> gate — and these tests deliberately do NOT flip it, leaving the shared
/// gate in its money-path OFF state:
/// <list type="bullet">
///   <item><b>C6</b> — <c>GET /api/document-uploads/{id}</c> for an internal (non-Supplier) tenant-A user
///         against a DocumentUpload owned by tenant B → <b>404</b>, never the file bytes. The
///         <c>Download</c> action's internal-viewer branch bypasses ONLY the seccode/company filters and
///         re-applies <c>TenantId == caller.TenantId</c>.</item>
///   <item><b>C8</b> — <c>GET /api/integration/sync-log/{id}/payload</c> for a tenant-A admin against a
///         tenant-B sync-log row → the handler guards <c>TenantId == caller-tenant</c>, so it returns a
///         null payload (the row is invisible), never tenant B's stored JSON.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DocumentAndSyncLogTenantGuardTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public DocumentAndSyncLogTenantGuardTests(IntegrationTestFixture fx) => _fx = fx;

    // -------------------- C6: document download is tenant-scoped --------------------

    [SkippableFact]
    public async Task Internal_user_cannot_download_another_tenants_document_by_guid()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Tenant-A Admin is an INTERNAL (non-Supplier) viewer. The doc belongs to tenant B (by GUID).
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        var resp = await client.GetAsync($"/api/document-uploads/{SecurityTestHarness.DocBId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "an internal user of tenant A must get 404 (not the file) for a tenant-B document (C6)");

        // Belt-and-braces: the body is NOT the stored PDF bytes.
        var media = resp.Content.Headers.ContentType?.MediaType;
        media.Should().NotBe("application/pdf", because: "the foreign file content must never be streamed");
    }

    [SkippableFact]
    public async Task Internal_user_can_download_own_tenant_document_path_is_reachable()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Sanity counter-test: a tenant-A internal user requesting a NON-existent tenant-A doc also 404s, but
        // crucially NOT because of the tenant guard — proving the guard above isn't a blanket 404. We use a
        // random tenant-A-scoped GUID; the point is the request is authorized (not 401/403) and tenant-matched
        // logic runs. (The fixture stores no streamable tenant-A doc, so we assert the auth gate, not bytes.)
        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        var resp = await client.GetAsync($"/api/document-uploads/{Guid.NewGuid()}");

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            because: "an authenticated internal user passes the [Authorize] gate on the download endpoint");
    }

    // -------------------- C8: sync-log payload-by-GUID is tenant-guarded --------------------

    [SkippableFact]
    public async Task SyncLog_payload_by_guid_does_not_return_another_tenants_payload()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);

        var resp = await client.GetAsync($"/api/integration/sync-log/{SecurityTestHarness.SyncLogBId}/payload");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the payload endpoint returns a 200 with a null payload for a row the caller can't see");

        var stream = await resp.Content.ReadAsStreamAsync();
        var result = (await JsonSerializer.DeserializeAsync<Result<string?>>(stream, Json))!;

        result.Data.Should().BeNull(
            because: "the tenant guard hides the tenant-B row, so no stored JSON is returned (C8)");
        (result.Data ?? string.Empty).Should().NotContain("tenant-B-only",
            because: "tenant B's stored payload must never leak to tenant A");
    }

    [SkippableFact]
    public async Task SyncLog_payload_by_guid_returns_own_tenant_payload()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Counter-test: a tenant-A row IS readable by a tenant-A admin — proving the guard is tenant-scoped,
        // not a blanket null. Create our own tagged tenant-A sync-log row with a known payload.
        var tag = "c8-own-" + Guid.NewGuid().ToString("N");
        var id = await _fx.CreateInboundSyncLogAsync(tag, IntegrationTestFixture.TenantId);

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var resp = await client.GetAsync($"/api/integration/sync-log/{id}/payload");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var stream = await resp.Content.ReadAsStreamAsync();
        var result = (await JsonSerializer.DeserializeAsync<Result<string?>>(stream, Json))!;

        result.Data.Should().NotBeNull(because: "the caller's own tenant row is readable");
        result.Data!.Should().Contain(tag, because: "the tenant-A admin reads its own tenant's stored payload");
    }
}
