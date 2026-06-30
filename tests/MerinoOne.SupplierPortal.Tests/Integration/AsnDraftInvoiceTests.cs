using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// Submitting an ASN must auto-create EXACTLY ONE draft Invoice spanning its PO(s) (the UQ_Invoice_asnId
/// upsert-or-skip in DraftInvoiceFromAsnFactory). Verified through the REAL host: push a PO inbound, create +
/// submit an ASN as the supplier, then assert one — and only one — Draft invoice exists for that ASN.
///
/// <para>Money path: scope gate OFF; fresh tagged supplier/PO per test.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnDraftInvoiceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnDraftInvoiceTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Submitting_an_asn_auto_creates_exactly_one_draft_invoice()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = ProcureToPayFlow.SimpleAsn(setup);
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        // R5 — submit through Send-for-Approval → buyer Approve (the draft invoice is created at the submit step).
        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = await Read<AsnDetailDto>(submitResp);
        submitted.Data!.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));
        submitted.Data!.DraftInvoiceId.Should().NotBeNull(because: "ASN submit auto-creates the draft invoice");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var invoices = await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.AsnId == asnId && !i.IsDeleted)
            .ToListAsync();
        invoices.Should().HaveCount(1, because: "exactly one draft invoice spans the submitted ASN's PO(s)");
        invoices[0].InvoiceStatus.Should().Be(InvoiceStatus.Draft);
        invoices[0].PurchaseOrderId.Should().Be(setup.PoId, because: "a single-PO ASN sets the scalar header PO");
        invoices[0].Id.Should().Be(submitted.Data!.DraftInvoiceId!.Value);
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
