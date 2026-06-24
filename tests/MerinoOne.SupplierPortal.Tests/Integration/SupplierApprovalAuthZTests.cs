using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// AuthZ regression (C1) for the privileged supplier lifecycle endpoints — approve / reject / verify-nic
/// all carry <c>[Authorize(Policy = "Supplier.Approve")]</c>. The policy resolves to a "permission" claim
/// check (<see cref="MerinoOne.SupplierPortal.Identity.PermissionRequirement"/>), so the test asserts the
/// end-to-end chain (UserRole → RolePermission → Permission → claim → policy):
/// <list type="bullet">
///   <item>A Supplier-role token (no Supplier.Approve) → <b>403 Forbidden</b>.</item>
///   <item>A Buyer-role token (no Supplier.Approve) → <b>403 Forbidden</b>.</item>
///   <item>No token at all → <b>401 Unauthorized</b>.</item>
///   <item>An Admin token (has Supplier.Approve) → NOT 401/403 (the authZ gate is passed; the request then
///         reaches the handler, which is what C1 is about — the endpoint is no longer open to suppliers).</item>
/// </list>
/// All tests run in the shared serial integration collection and only READ the fixture/harness seed.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SupplierApprovalAuthZTests
{
    private readonly IntegrationTestFixture _fx;
    public SupplierApprovalAuthZTests(IntegrationTestFixture fx) => _fx = fx;

    private static readonly Guid AnySupplierId = IntegrationTestFixture.SupplierId;

    // The bodies are well-formed so a 400 (validation) never masquerades as the authZ outcome under test.
    private static HttpContent ApproveBody() => JsonContent.Create(new { overrideComment = "ok" });
    private static HttpContent RejectBody()  => JsonContent.Create(new { reason = "not eligible" });
    private static HttpContent VerifyBody()  => JsonContent.Create(new { types = new[] { "GST" } });

    public static IEnumerable<object[]> PrivilegedEndpoints() => new[]
    {
        new object[] { $"/api/suppliers/{AnySupplierId}/approve" },
        new object[] { $"/api/suppliers/{AnySupplierId}/reject" },
        new object[] { $"/api/suppliers/{AnySupplierId}/verify-nic" },
    };

    private static HttpContent BodyFor(string path) =>
        path.EndsWith("/approve") ? ApproveBody()
        : path.EndsWith("/reject") ? RejectBody()
        : VerifyBody();

    // -------------------- 401: no token --------------------

    [SkippableTheory]
    [MemberData(nameof(PrivilegedEndpoints))]
    public async Task Privileged_endpoint_without_token_is_401(string path)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = _fx.Factory.CreateClient();
        var resp = await client.PostAsync(path, BodyFor(path));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "an unauthenticated caller must be challenged before any handler runs");
    }

    // -------------------- 403: Supplier-role token (C1 regression) --------------------

    [SkippableTheory]
    [MemberData(nameof(PrivilegedEndpoints))]
    public async Task Privileged_endpoint_as_supplier_is_403(string path)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier);
        var resp = await client.PostAsync(path, BodyFor(path));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "a Supplier-role principal lacks Supplier.Approve — it must NOT approve/reject/verify (C1)");
    }

    // -------------------- 403: Buyer-role token (no Supplier.Approve) --------------------

    [SkippableTheory]
    [MemberData(nameof(PrivilegedEndpoints))]
    public async Task Privileged_endpoint_as_buyer_is_403(string path)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer);
        var resp = await client.PostAsync(path, BodyFor(path));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "Buyer does not hold Supplier.Approve in the permission matrix");
    }

    // -------------------- Admin passes the gate (NOT 401/403) --------------------

    [SkippableTheory]
    [MemberData(nameof(PrivilegedEndpoints))]
    public async Task Privileged_endpoint_as_admin_passes_authz_gate(string path)
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var resp = await client.PostAsync(path, BodyFor(path));

        // The authZ assertion is the point: an Admin holding Supplier.Approve is NOT blocked by the policy.
        // The request reaches the handler; the concrete handler result (200 / 404 / 409) is out of scope here.
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            because: "an Admin holds Supplier.Approve, so the policy must let the request through to the handler");
    }

    // -------------------- Admin approve happy-path on a verified supplier returns 2xx --------------------

    [SkippableFact]
    public async Task Admin_approve_on_fresh_supplier_returns_2xx()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Create our OWN tagged supplier (never mutate the shared fixture seed). It has no Fail verifications,
        // so approve has no override-required validation block; no contacts, so the handler logs
        // "approved without contacts" and returns 200.
        var tag = "approve-" + Guid.NewGuid().ToString("N");
        var supplier = await _fx.CreateSupplierAsync(tag, IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);

        var client = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var resp = await client.PostAsync($"/api/suppliers/{supplier.SupplierId}/approve", ApproveBody());

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "an Admin with Supplier.Approve can approve a supplier with no failed NIC checks");
    }
}
