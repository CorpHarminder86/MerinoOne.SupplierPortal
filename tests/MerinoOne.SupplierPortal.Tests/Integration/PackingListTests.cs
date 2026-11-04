using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R18 — the ASN Packing List (<c>GET /api/asns/{id}/packing-list</c>) on REAL SQL through the real host.
///
/// <para><b>The endpoint</b>: a supplier that needs buyer confirmation gets a 409 until the buyer approves and a real PDF
/// from then on; one that needs none prints from Draft; a Cancelled ASN has none for anybody; and an ASN the caller's
/// row-level security hides — another supplier's (seccode) or another company's (tenant / company filters, gate ON) — is
/// a 404, decided before availability, so it cannot be used to probe an ASN's status.</para>
///
/// <para><b>The read model</b> (<see cref="GetPackingListQuery"/>) is run through the app's mediator, under the Supplier-role
/// test user so the seccode filter applies exactly as on a real call, and asserted field by field: header references,
/// the supplier / ship-from / ship-to parties (with their fallbacks), delivery terms, the lines and totals, and the packing
/// list date. Every test builds its own tagged supplier / PO / ASN — the test DB persists between runs — and the only flag
/// it flips (<c>AsnConfirmationRequired</c>) is on that tagged supplier.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PackingListTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public PackingListTests(IntegrationTestFixture fx) => _fx = fx;

    private static string Url(Guid asnId) => $"/api/asns/{asnId}/packing-list";

    // ═══════════════════════════════════ The endpoint ═══════════════════════════════════

    [SkippableFact]
    public async Task A_supplier_needing_confirmation_is_refused_until_the_buyer_approves_then_downloads_a_real_pdf()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var asn = await NewDraftAsync();

        var draft = await asn.Supplier.GetAsync(Url(asn.AsnId));
        draft.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await Body(draft));
        (await Errors(draft)).Should().ContainSingle().Which
            .Should().Contain("'Draft'").And.Contain("Approved", because: "the 409 names the status and the rule");

        await SendForApprovalAsync(asn);
        (await asn.Supplier.GetAsync(Url(asn.AsnId))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, because: "the buyer has not decided yet");

        await ApproveAsync(asn.AsnId);
        var approved = await asn.Supplier.GetAsync(Url(asn.AsnId));
        approved.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approved));
        approved.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");

        var bytes = await approved.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(1000, because: "a rendered A4 document is never a stub");
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF", because: "the payload is a real PDF document");

        var disposition = approved.Content.Headers.ContentDisposition!;
        $"{disposition.FileName}{disposition.FileNameStar}".Should().Contain($"PackingList-{asn.AsnNumber}.pdf");
    }

    [SkippableFact]
    public async Task A_supplier_needing_no_confirmation_prints_from_draft_and_loses_that_when_the_flag_flips_back()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var asn = await NewDraftAsync();

        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, asn.Setup.Supplier.SupplierId, required: false);
        try
        {
            var draft = await asn.Supplier.GetAsync(Url(asn.AsnId));
            draft.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(draft));
            draft.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        }
        finally
        {
            await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, asn.Setup.Supplier.SupplierId, required: true);
        }

        (await asn.Supplier.GetAsync(Url(asn.AsnId))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, because: "the same Draft is unavailable again once the supplier needs confirmation");
    }

    [SkippableFact]
    public async Task A_cancelled_asn_has_no_packing_list_even_for_a_supplier_needing_no_confirmation()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var asn = await NewPrintableAsnAsync();
        (await asn.Supplier.GetAsync(Url(asn.AsnId))).StatusCode
            .Should().Be(HttpStatusCode.OK, because: "control: the Draft prints, so the 409 below is the cancel's doing");

        var cancel = await asn.Supplier.PostAsJsonAsync($"/api/asns/{asn.AsnId}/cancel", new { });
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(cancel));

        var after = await asn.Supplier.GetAsync(Url(asn.AsnId));
        after.StatusCode.Should().Be(HttpStatusCode.Conflict, because: await Body(after));
        (await Errors(after)).Should().ContainSingle().Which.Should().Contain("'Cancelled'");
    }

    [SkippableFact]
    public async Task A_user_of_another_supplier_gets_404_for_a_printable_and_for_an_unprintable_asn_alike()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // ASNs of a supplier the Supplier-role user holds NO SecRight on — the second proves the 404 is decided before the 409.
        var (printable, unprintable) = await SeedPrintableAndUnprintableAsnsAsync();

        // Controls: a privileged (admin) caller sees both, so a 404 below is row-level security, not a missing row.
        var admin = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var adminPdf = await admin.GetAsync(Url(printable));
        adminPdf.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(adminPdf));
        Encoding.ASCII.GetString(await adminPdf.Content.ReadAsByteArrayAsync(), 0, 4).Should().Be("%PDF");
        (await admin.GetAsync(Url(unprintable))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, because: "control: to a caller who can see it the Draft is unavailable");

        var stranger = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        (await stranger.GetAsync(Url(printable))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync(Url(unprintable))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, because: "a caller outside the scope must not learn the ASN exists, let alone its status");
    }

    [SkippableFact]
    public async Task A_user_of_another_company_gets_404_for_a_printable_and_for_an_unprintable_asn_alike()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var (printable, unprintable) = await SeedPrintableAndUnprintableAsnsAsync();
        var owner = await _fx.ClientAsAsync(SecurityTestHarness.Users.Admin, IntegrationTestFixture.CompanyId);
        var otherCompany = await _fx.ClientAsAsync(SecurityTestHarness.Users.AdminB, SecurityTestHarness.CompanyBId);

        // Control: with the scope gate OFF only the seccode filter applies, and a privileged admin of ANOTHER company passes
        // it — so the 404s below are the tenant / company filters' doing, not a missing row or a missing role.
        var open = await otherCompany.GetAsync(Url(printable));
        open.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(open));

        await using var _ = await _fx.EnableScopeFiltersAsync();

        // Controls: the owning company's admin still reads both, so the 404s are not the gate hiding everything.
        var ownerPdf = await owner.GetAsync(Url(printable));
        ownerPdf.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(ownerPdf));
        (await owner.GetAsync(Url(unprintable))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, because: "control: to a caller who can see it the Draft is unavailable");

        (await otherCompany.GetAsync(Url(printable))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherCompany.GetAsync(Url(unprintable))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, because: "another company must not learn the ASN exists, let alone its status");
    }

    // ═══════════════════════════════════ The read model ═══════════════════════════════════

    [SkippableFact]
    public async Task The_query_maps_the_header_references_parties_and_line_of_the_asn()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var unit = $"NOUNIT-{tag}";   // absent from the unit master → the resolver's fallback of 3 decimals
        var irn = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");   // 64 characters
        var asn = await NewPrintableAsnAsync(
            beforeCreate: setup => SetPoLineUnitAsync(setup.PoLineId, unit),
            shape: r => r with
            {
                PackingList = $"PL-{tag}", VehicleNumber = "MH12AB1234", TrackingNumber = $"TRK-{tag}",
                Notes = "Keep dry.\r\nDeliver before noon.",
                EwayBillNo = $"EWB{tag}", EwayBillDate = new DateOnly(2026, 11, 2), IrnNo = irn, IrnDate = new DateOnly(2026, 11, 1),
            });

        // The carrier is read from what the ASN froze, never re-joined to the master: freeze a name the master does not have.
        await WithAsnAsync(asn.AsnId, a => a.CarrierName = $"Frozen {tag}");
        var row = await LoadAsnAsync(asn.AsnId);
        var company = await CompanyNameAsync(row.TenantEntityId!.Value);
        var snapshot = row.WarehouseAddressSnapshot!;

        var dto = await QueryAsync(asn.AsnId);

        // header references
        dto.AsnNumber.Should().Be(asn.AsnNumber);
        dto.PurchaseOrders.Should().Be(asn.Setup.PoNumber);
        dto.PackingListNumber.Should().Be($"PL-{tag}");
        dto.PackingListDate.Should().Be(row.CreatedOn, because: "an ASN that has not been posted dates its packing list from its creation");
        dto.ShipmentNumber.Should().BeNull(because: "the LN shipment number only exists once LN has acknowledged the ASN");
        dto.SupplierCode.Should().Be(asn.Setup.Supplier.SupplierCode);
        dto.Currency.Should().Be("INR");
        dto.Carrier.Should().Be($"{ProcureToPayFlow.TestCarrierCode} – Frozen {tag}");
        dto.VehicleNo.Should().Be("MH12AB1234");
        dto.TrackingNo.Should().Be($"TRK-{tag}");
        dto.EwayBillNo.Should().Be($"EWB{tag}");
        dto.EwayBillDate.Should().Be(new DateOnly(2026, 11, 2));
        dto.IrnNo.Should().Be(irn);
        dto.IrnDate.Should().Be(new DateOnly(2026, 11, 1));
        dto.Remarks.Should().Be($"This packing list is generated based on ASN {asn.AsnNumber}.\nKeep dry. Deliver before noon.");

        // parties
        dto.Supplier.Name.Should().Be($"Test Supplier {asn.Setup.Tag}");
        dto.Supplier.Cin.Should().BeNull(because: "the portal holds no CIN");
        dto.Supplier.GstNo.Should().BeNull(because: "the tagged supplier has no GST number");
        dto.Supplier.AddressLines.Should().BeEmpty();
        dto.Supplier.Phone.Should().BeNull();
        dto.Supplier.Email.Should().BeNull();
        dto.ShipFrom.Name.Should().Be(dto.Supplier.Name);
        dto.ShipTo.Name.Should().Contain(company).And.Be($"{company} – {snapshot.AddressName}");
        dto.ShipTo.AddressLines.Should().NotBeEmpty().And.StartWith(snapshot.Line1!);
        dto.ShipTo.AddressLines[^1].Should().Contain(snapshot.City!).And.Contain(snapshot.Pincode!);

        // the one line
        var line = dto.Lines.Should().ContainSingle().Subject;
        line.SlNo.Should().Be(1);
        line.PoNumber.Should().Be(asn.Setup.PoNumber);
        line.PoLine.Should().Be(asn.Setup.PoPositionNo);
        line.Seq.Should().Be(1);
        line.Position.Should().BeNull(because: "the portal has no separate position value");
        line.ItemCode.Should().Be(asn.Setup.ItemCode);
        line.ShippedQty.Should().Be(asn.Setup.OrderQty);
        line.Uom.Should().Be(unit, because: "the unit of measure is the PO line's order unit");
        line.QtyDecimals.Should().Be(3, because: "an unknown unit resolves to 3 decimal places");
        line.SerialLotNumbers.Should().BeEmpty();
        line.ExpiryDate.Should().BeNull();

        dto.TotalShippedQty.Should().Be(asn.Setup.OrderQty);
        dto.TotalQtyDecimals.Should().Be(3);
        dto.GeneratedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
    }

    [SkippableFact]
    public async Task Delivery_terms_come_from_the_po_and_fall_back_to_the_supplier_master()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var asn = await NewPrintableAsnAsync();

        (await QueryAsync(asn.AsnId)).DeliveryTerms.Should().BeNull(because: "neither the PO nor the supplier master carries terms yet");

        await SetSupplierDeliveryTermCodeAsync(asn.Setup.Supplier.SupplierId, $"FOB-{tag}");
        (await QueryAsync(asn.AsnId)).DeliveryTerms.Should().Be($"FOB-{tag}", because: "no PO terms, so the supplier master's code");

        await SetPoDeliveryTermsAsync(asn.Setup.PoId, $"CIF-{tag}");
        (await QueryAsync(asn.AsnId)).DeliveryTerms.Should().Be($"CIF-{tag}", because: "the PO's own terms win");
    }

    [SkippableFact]
    public async Task Supplier_and_ship_from_use_the_registered_and_shipping_addresses_and_the_primary_contact_else_the_first_ones()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var asn = await NewPrintableAsnAsync();
        var supplierId = asn.Setup.Supplier.SupplierId;

        // Inserted one by one so creation order (Seq) is exactly this order.
        await AddAddressAsync(supplierId, "Billing", "1 Billing Rd", null, "Pune", "411001");
        await AddAddressAsync(supplierId, "Shipping", "2 Dock Rd", "Gate 3, ", "Thane", "400601");
        await AddAddressAsync(supplierId, "Registered", "3 Reg St", null, "Mumbai", "400001");
        await AddContactAsync(supplierId, "First Contact", "first@example.test", "111", isPrimary: false);
        await AddContactAsync(supplierId, "Primary Contact", "primary@example.test", "222", isPrimary: true);

        var preferred = await QueryAsync(asn.AsnId);
        preferred.Supplier.AddressLines.Should().Equal("3 Reg St", "Mumbai - 400001, Maharashtra, India");
        preferred.ShipFrom.AddressLines.Should().Equal("2 Dock Rd", "Gate 3", "Thane - 400601, Maharashtra, India");
        preferred.Supplier.Phone.Should().Be("222");
        preferred.Supplier.Email.Should().Be("primary@example.test");

        // Take the preferred ones away: the first address (by creation) and the first contact stand in.
        await RemovePreferredSupplierDetailsAsync(supplierId);

        var fallback = await QueryAsync(asn.AsnId);
        fallback.Supplier.AddressLines.Should().Equal("1 Billing Rd", "Pune - 411001, Maharashtra, India");
        fallback.ShipFrom.AddressLines.Should().Equal(fallback.Supplier.AddressLines, because: "Ship From falls back to the header address");
        fallback.Supplier.Phone.Should().Be("111");
        fallback.Supplier.Email.Should().Be("first@example.test");
    }

    [SkippableFact]
    public async Task Ship_to_falls_back_from_the_frozen_snapshot_to_the_live_warehouse_then_to_the_company_base_address()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var asn = await NewPrintableAsnAsync();
        var company = await CompanyNameAsync((await LoadAsnAsync(asn.AsnId)).TenantEntityId!.Value);
        var fromSnapshot = (await QueryAsync(asn.AsnId)).ShipTo;
        fromSnapshot.Name.Should().StartWith($"{company} – ");

        // Snapshot gone → the live CompanyAddress the ASN points at: the same warehouse, the same address.
        await WithAsnAsync(asn.AsnId, a => a.WarehouseAddressSnapshot = null);
        (await QueryAsync(asn.AsnId)).ShipTo.Should().BeEquivalentTo(fromSnapshot);

        // Warehouse link gone too → the company's base address: its lines, and just the company name (a base address is no warehouse).
        await WithAsnAsync(asn.AsnId, a => a.WarehouseAddressId = null);
        var fromBase = (await QueryAsync(asn.AsnId)).ShipTo;
        fromBase.Name.Should().Be(company);
        fromBase.AddressLines.Should().NotBeEmpty().And.StartWith(await BaseAddressLine1Async());
        fromBase.AddressLines.Should().NotEqual(fromSnapshot.AddressLines, because: "it is the company's own address, not the warehouse's");
    }

    [SkippableFact]
    public async Task A_multi_po_asn_lists_every_po_and_totals_its_lines()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var first = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m);
        var second = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, first.Supplier, orderQty: 6m, confirm: true);
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, first.Supplier.SupplierId, required: false);

        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var request = ProcureToPayFlow.SimpleAsn(first) with
        {
            PurchaseOrderId = null,
            PurchaseOrderIds = new[] { first.PoId, second.PoId },
            Lines = new List<CreateAsnLineRequest>
            {
                new(first.PoLineId, ShippedQty: 10m, BatchNumber: null, ExpiryDate: null),
                new(second.PoLineId, ShippedQty: 4m, BatchNumber: null, ExpiryDate: null),
            },
        };
        var create = await supplier.PostAsJsonAsync("/api/asns", request);
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        var dto = await QueryAsync(asnId);

        dto.PurchaseOrders!.Split(", ").Should().BeEquivalentTo(new[] { first.PoNumber, second.PoNumber });
        dto.Lines.Select(l => l.SlNo).Should().Equal(1, 2);
        dto.Lines.Single(l => l.PoNumber == first.PoNumber).ShippedQty.Should().Be(10m);
        dto.Lines.Single(l => l.PoNumber == second.PoNumber).ShippedQty.Should().Be(4m);
        dto.TotalShippedQty.Should().Be(14m);
    }

    [SkippableFact]
    public async Task Serial_and_lot_numbers_reach_the_packing_list_with_their_expiry_dates()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var unit = $"NOUNIT-{tag}";   // 3 decimals, so a lot quantity prints as "4.000"
        var serialPo = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 3m);
        var lotPo = await ProcureToPayFlow.SeedPoForSupplierAsync(_fx, serialPo.Supplier, orderQty: 6m, confirm: true);
        await SetItemControlAsync(serialPo.ItemCode, serialized: true);
        await SetItemControlAsync(lotPo.ItemCode, lotControlled: true);
        await SetPoLineUnitAsync(serialPo.PoLineId, unit);
        await SetPoLineUnitAsync(lotPo.PoLineId, unit);
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, serialPo.Supplier.SupplierId, required: false);

        var jan = new DateOnly(2027, 1, 31);
        var mar = new DateOnly(2027, 3, 31);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var request = ProcureToPayFlow.SimpleAsn(serialPo) with
        {
            PurchaseOrderId = null,
            PurchaseOrderIds = new[] { serialPo.PoId, lotPo.PoId },
            Lines = new List<CreateAsnLineRequest>
            {
                // three serials that all expire together; two lots that expire on different dates
                new(serialPo.PoLineId, ShippedQty: 3m, BatchNumber: null, ExpiryDate: null,
                    Serials: new List<AsnLineSerialInput> { new($"SN-{tag}-1", jan), new($"SN-{tag}-2", jan), new($"SN-{tag}-3", jan) }),
                new(lotPo.PoLineId, ShippedQty: 6m, BatchNumber: null, ExpiryDate: null,
                    Lots: new List<AsnLineLotInput> { new($"LOT-{tag}-A", 4m, jan), new($"LOT-{tag}-B", 2m, mar) }),
            },
        };
        var create = await supplier.PostAsJsonAsync("/api/asns", request);
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        var dto = await QueryAsync(asnId);

        var serialLine = dto.Lines.Single(l => l.PoNumber == serialPo.PoNumber);
        serialLine.SerialLotNumbers.Should().Equal($"SN-{tag}-1", $"SN-{tag}-2", $"SN-{tag}-3");
        serialLine.ExpiryDate.Should().Be("2027-01-31", because: "every serial shares it, so it moves to the expiry column");

        var lotLine = dto.Lines.Single(l => l.PoNumber == lotPo.PoNumber);
        lotLine.SerialLotNumbers.Should().Equal($"LOT-{tag}-A (4.000) · exp 2027-01-31", $"LOT-{tag}-B (2.000) · exp 2027-03-31");
        lotLine.ExpiryDate.Should().BeNull(because: "the lots expire on different dates, so each carries its own");
    }

    [SkippableFact]
    public async Task The_packing_list_date_is_the_creation_date_until_the_asn_is_posted_then_the_post_date()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var asn = await NewDraftAsync();
        await SendForApprovalAsync(asn);
        await ApproveAsync(asn.AsnId);

        var created = (await LoadAsnAsync(asn.AsnId)).CreatedOn;
        (await QueryAsync(asn.AsnId)).PackingListDate.Should().Be(created);

        await ProcureToPayFlow.EnsureShipmentRefsAsync(_fx, asn.AsnId);
        var post = await ProcureToPayFlow.PostAsync(asn.Supplier, asn.AsnId);
        post.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(post));

        var posted = (await LoadAsnAsync(asn.AsnId)).PostedAt;
        posted.Should().NotBeNull(because: "Post stamps the shipping date");
        var after = await QueryAsync(asn.AsnId);
        after.PackingListDate.Should().Be(posted);
        after.PackingListDate.Should().BeOnOrAfter(created);
        after.PackingListNumber.Should().Be("PL-TEST-1", because: "the packing list reference the supplier entered travels with it");

        (await asn.Supplier.GetAsync(Url(asn.AsnId))).StatusCode
            .Should().Be(HttpStatusCode.OK, because: "a posted ASN prints for every supplier");
    }

    // ═══════════════════════════════════ arrange ═══════════════════════════════════

    private sealed record NewAsn(Guid AsnId, string AsnNumber, ProcureToPayFlow.Setup Setup, HttpClient Supplier);

    /// <summary>
    /// A Draft ASN shipping the whole of a fresh single-line PO, created through the real API by the Supplier-role test user
    /// (granted write on the tagged supplier). <paramref name="beforeCreate"/> may adjust the PO first, <paramref name="shape"/> the request.
    /// </summary>
    private async Task<NewAsn> NewDraftAsync(
        Func<ProcureToPayFlow.Setup, Task>? beforeCreate = null, Func<CreateAsnRequest, CreateAsnRequest>? shape = null)
    {
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        if (beforeCreate is not null) await beforeCreate(setup);

        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var request = ProcureToPayFlow.SimpleAsn(setup);
        var create = await supplier.PostAsJsonAsync("/api/asns", shape is null ? request : shape(request));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var created = (await Read<AsnDetailDto>(create)).Data!;
        return new NewAsn(created.Id, created.AsnNumber, setup, supplier);
    }

    /// <summary>A Draft ASN of a supplier needing no buyer confirmation — printable straight away, no approval round trip.</summary>
    private async Task<NewAsn> NewPrintableAsnAsync(
        Func<ProcureToPayFlow.Setup, Task>? beforeCreate = null, Func<CreateAsnRequest, CreateAsnRequest>? shape = null)
    {
        var asn = await NewDraftAsync(beforeCreate, shape);
        await ProcureToPayFlow.SetAsnConfirmationRequiredAsync(_fx, asn.Setup.Supplier.SupplierId, required: false);
        return asn;
    }

    private async Task SendForApprovalAsync(NewAsn asn)
    {
        var send = await asn.Supplier.PostAsJsonAsync($"/api/asns/{asn.AsnId}/send-for-approval", new SendForApprovalRequest());
        send.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(send));
    }

    private async Task ApproveAsync(Guid asnId)
    {
        var buyer = await _fx.ClientAsAsync(SecurityTestHarness.Users.Buyer, IntegrationTestFixture.CompanyId);
        var approve = await buyer.PostAsJsonAsync($"/api/asns/{asnId}/approve", new ApproveAsnRequest());
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));
    }

    /// <summary>
    /// A fresh supplier (of the fixture company, granted to no test user) with a Submitted ASN — printable — and a Draft one
    /// that is not, because the supplier needs buyer confirmation.
    /// </summary>
    private async Task<(Guid Printable, Guid Unprintable)> SeedPrintableAndUnprintableAsnsAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync($"pl{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        return (await SeedBareAsnAsync(supplier, $"ASN-PLP-{tag}", AsnStatus.Submitted),
                await SeedBareAsnAsync(supplier, $"ASN-PLD-{tag}", AsnStatus.Draft));
    }

    /// <summary>An ASN row with no lines and no PO, owned by <paramref name="supplier"/> — enough for the read, none of the lifecycle.</summary>
    private async Task<Guid> SeedBareAsnAsync(SecurityTestHarness.SeededSupplier supplier, string asnNumber, AsnStatus status)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asn = new Asn
        {
            Id = Guid.NewGuid(), AsnNumber = asnNumber, SupplierId = supplier.SupplierId,
            ExpectedDeliveryDate = DateTime.UtcNow.Date.AddDays(1), AsnStatus = status,
            SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        };
        db.Asns.Add(asn);
        await db.SaveChangesAsync();
        return asn.Id;
    }

    private async Task SetPoLineUnitAsync(Guid poLineId, string unit)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.Id == poLineId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.OrderUnit, unit));
    }

    /// <summary>Flags a (tagged, test-owned) item of the fixture company serial- or lot-controlled — at most one, per the Item XOR check.</summary>
    private async Task SetItemControlAsync(string itemCode, bool serialized = false, bool lotControlled = false)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Items.IgnoreQueryFilters()
            .Where(i => i.TenantEntityId == IntegrationTestFixture.CompanyId && i.Code == itemCode)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.IsSerialized, serialized)
                .SetProperty(i => i.IsLotControlled, lotControlled));
    }

    private async Task SetSupplierDeliveryTermCodeAsync(Guid supplierId, string code)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Suppliers.IgnoreQueryFilters().Where(s => s.Id == supplierId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryTermCode, code));
    }

    private async Task SetPoDeliveryTermsAsync(Guid poId, string terms)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == poId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeliveryTerms, terms));
    }

    private async Task AddAddressAsync(Guid supplierId, string type, string line1, string? line2, string city, string pincode)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierAddresses.Add(new SupplierAddress
        {
            Id = Guid.NewGuid(), SupplierId = supplierId, AddressType = type, AddressLine1 = line1, AddressLine2 = line2,
            City = city, State = "Maharashtra", Pincode = pincode, Country = "India", CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task AddContactAsync(Guid supplierId, string name, string email, string phone, bool isPrimary)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierContacts.Add(new SupplierContact
        {
            Id = Guid.NewGuid(), SupplierId = supplierId, ContactName = name, Email = email, Phone = phone,
            IsPrimary = isPrimary, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Soft-deletes the Registered and Shipping addresses and demotes the primary contact.</summary>
    private async Task RemovePreferredSupplierDetailsAsync(Guid supplierId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.SupplierAddresses.IgnoreQueryFilters()
            .Where(a => a.SupplierId == supplierId && (a.AddressType == "Registered" || a.AddressType == "Shipping"))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDeleted, true));
        await db.SupplierContacts.IgnoreQueryFilters()
            .Where(c => c.SupplierId == supplierId && c.IsPrimary)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsPrimary, false));
    }

    private async Task WithAsnAsync(Guid asnId, Action<Asn> change)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asn = await db.Asns.IgnoreQueryFilters().FirstAsync(a => a.Id == asnId);
        change(asn);
        await db.SaveChangesAsync();
    }

    // ═══════════════════════════════════ act / read ═══════════════════════════════════

    /// <summary>
    /// Runs <see cref="GetPackingListQuery"/> through the app's mediator as the Supplier-role test user. No request is in
    /// flight here, so the scope's current user would be anonymous and the seccode filter would hide every ASN; putting the
    /// user's principal on the HTTP-context accessor (async-local, so it ends with this call) makes the query run under the
    /// same row-level security a real request does.
    /// </summary>
    private async Task<PackingListDto> QueryAsync(Guid asnId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("userCode", SecurityTestHarness.Users.Supplier),
                new Claim(ClaimTypes.Role, RoleNames.Supplier),
                new Claim("tenant", IntegrationTestFixture.TenantId.ToString()),
            }, authenticationType: "test")),
        };
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GetPackingListQuery(asnId));
    }

    private async Task<Asn> LoadAsnAsync(Guid asnId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Asns.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == asnId);
    }

    private async Task<string> CompanyNameAsync(Guid companyId)
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.TenantEntities.IgnoreQueryFilters().Where(e => e.Id == companyId).Select(e => e.Name).SingleAsync();
    }

    private async Task<string> BaseAddressLine1Async()
    {
        await using var scope = _fx.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line1 = await db.CompanyAddresses.IgnoreQueryFilters()
            .Where(a => a.TenantEntityId == IntegrationTestFixture.CompanyId && a.IsBaseAddress && !a.IsDeleted)
            .Select(a => a.AddressLine1)
            .SingleAsync();
        return line1.Trim();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    /// <summary>The error messages of a failed response (read from the envelope: the raw body escapes an apostrophe as ').</summary>
    private static async Task<List<string>> Errors(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result>(stream, Json))!.Errors;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
