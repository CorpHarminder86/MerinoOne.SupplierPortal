using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R4 Module 2 — supplier change request → approve → applier, through the REAL host. A Supplier user raises a
/// change request carrying three heterogeneous deltas:
/// <list type="bullet">
///   <item>a scalar <c>Edit</c> on Supplier.Website;</item>
///   <item>an <c>Add</c>-Address (a brand-new address row);</item>
///   <item>an <c>Add</c>-Contact whose payload <c>addressId</c> links to a PRE-EXISTING address of the supplier.</item>
/// </list>
/// On submit + Admin approve, the deltas must apply atomically to the LIVE supplier data: the website changes,
/// the new address exists, and the new contact's AddressId resolves to the linked pre-existing address. AuthZ:
/// a Buyer (no <c>Supplier.ApproveChange</c>) must get 403 when attempting the approve.
///
/// <para>Money path: scope gate OFF. A fresh tagged supplier per test; the Supplier-role user is granted
/// read+write on its seccode so the SupplierWriteGuard admits the change-request create.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SupplierChangeRequestApplierTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public SupplierChangeRequestApplierTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Approve_applies_website_edit_add_address_and_add_contact_with_address_link()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];

        // Fresh tagged supplier under the fixture tenant/company; grant the Supplier user read+write on its seccode.
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);

        // Seed a PRE-EXISTING address on the supplier (the Add-Contact will link to THIS address by id).
        var existingAddressId = await SeedAddressAsync(supplier.SupplierId, $"existing-{tag}");

        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var newWebsite = $"https://changed-{tag}.example.test";
        var newContactEmail = $"new-contact-{tag}@supplier.test";
        var newAddressLine1 = $"99 Added Lane {tag}";

        var create = new CreateSupplierChangeRequestRequest(
            SupplierId: supplier.SupplierId,
            Summary: $"CR {tag}",
            Lines: new List<SupplierChangeLineInput>
            {
                // 1) scalar Edit on the Supplier aggregate (no targetEntityId needed).
                new(TargetEntity: "Supplier", Operation: "Edit", FieldName: "Website", NewValue: newWebsite),
                // 2) Add a brand-new address.
                new(TargetEntity: "Address", Operation: "Add",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        addressType = "Billing",
                        addressLine1 = newAddressLine1,
                        city = "Pune",
                        state = "Maharashtra",
                        pincode = "411001",
                        country = "India",
                    })),
                // 3) Add a contact whose addressId links to the PRE-EXISTING address.
                new(TargetEntity: "Contact", Operation: "Add",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        contactName = "Linked Contact",
                        email = newContactEmail,
                        isPrimary = false,
                        addressId = existingAddressId.ToString(),
                    })),
            });

        // Create (Draft).
        var createResp = await supplierClient.PostAsJsonAsync("/api/suppliers/change-requests", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var crId = (await Read<SupplierChangeRequestDto>(createResp)).Data!.Id;

        // Submit (→ Submitted).
        var submitResp = await supplierClient.PostAsync($"/api/suppliers/change-requests/{crId}/submit", null);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));

        // AuthZ guard: a Buyer (no Supplier.ApproveChange) is FORBIDDEN to approve.
        var buyerClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var buyerApprove = await buyerClient.PostAsync($"/api/suppliers/change-requests/{crId}/approve", null);
        buyerApprove.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "a Buyer lacks Supplier.ApproveChange and must not approve a change request");

        // Admin approves → the deltas apply atomically to the live supplier data.
        var adminClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var approveResp = await adminClient.PostAsync($"/api/suppliers/change-requests/{crId}/approve", null);
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approveResp));

        // Assert the live data at the SQL boundary.
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // (a) the scalar Website edit landed on the live supplier.
        var live = await db.Suppliers.IgnoreQueryFilters().FirstAsync(s => s.Id == supplier.SupplierId);
        live.Website.Should().Be(newWebsite, because: "the Supplier.Website Edit delta is applied on approve");

        // (b) the Add-Address inserted a new live address row.
        var addedAddress = await db.SupplierAddresses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.SupplierId == supplier.SupplierId && a.AddressLine1 == newAddressLine1 && !a.IsDeleted);
        addedAddress.Should().NotBeNull(because: "the Add-Address delta inserts a live address row");

        // (c) the Add-Contact's AddressId resolves to the PRE-EXISTING address (the link is honoured).
        var addedContact = await db.SupplierContacts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.SupplierId == supplier.SupplierId && c.Email == newContactEmail && !c.IsDeleted);
        addedContact.Should().NotBeNull(because: "the Add-Contact delta inserts a live contact row");
        addedContact!.AddressId.Should().Be(existingAddressId,
            because: "the contact's payload addressId resolves to the supplier's pre-existing address (R4 contact↔address link)");

        // The request rolled forward past Approved (Pushed/PartiallyPushed in Mock).
        var cr = await db.SupplierChangeRequests.IgnoreQueryFilters().FirstAsync(r => r.Id == crId);
        cr.ChangeStatus.ToString().Should().BeOneOf(
            new[] { "Approved", "Pushed", "PartiallyPushed", "PushFailed" },
            because: "approval flips the request out of Submitted and the post-commit push rolls it up");
    }

    /// <summary>Seeds a non-deleted address on the supplier directly via the DbContext; returns its id.</summary>
    private async Task<Guid> SeedAddressAsync(Guid supplierId, string tag)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        db.SupplierAddresses.Add(new SupplierAddress
        {
            Id = id,
            SupplierId = supplierId,
            AddressType = "Registered",
            AddressLine1 = $"Pre-existing {tag}",
            City = "Mumbai",
            State = "Maharashtra",
            Pincode = "400001",
            Country = "India",
            CreatedBy = "seed",
            CreatedOn = now,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
