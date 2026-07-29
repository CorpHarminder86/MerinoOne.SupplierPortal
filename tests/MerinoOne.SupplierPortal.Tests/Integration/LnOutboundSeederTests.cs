using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R9 — LnOutboundSeeder: 7 Legacy rows per tenant (8 before R12 retired PoAcknowledge), twice-idempotent,
/// and the per-slot hash-gate matrix (hand-edit survives; untouched row whose repo default changed is updated).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LnOutboundSeederTests
{
    private readonly IntegrationTestFixture _fx;
    public LnOutboundSeederTests(IntegrationTestFixture fx) => _fx = fx;

    private async Task SeedAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await LnOutboundSeeder.SeedAsync(db);
    }

    [SkippableFact]
    public async Task Seeds_seven_legacy_rows_per_tenant_and_is_idempotent()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedAsync();
        await SeedAsync(); // twice — idempotent

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Kind filter (R10): the shared test tenant also accrues Document-kind rows from the IDM dispatch tests.
        var rows = await db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == IntegrationTestFixture.TenantId && c.Kind == OutboundIntegrationKind.Transaction && !c.IsDeleted)
            .ToListAsync();

        // R12 (D14) — seven, not eight: PoAcknowledge no longer posts to LN, and a pre-R12 row for it is
        // soft-deleted on re-seed.
        rows.Should().HaveCount(7);
        rows.Should().NotContain(r => r.TransactionType == OutboxTransactionType.PoAcknowledge);
        rows.Should().OnlyContain(r => r.DispatchMode == OutboundDispatchMode.Legacy, "seeded rows must never change dispatch behaviour");
        rows.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.RequestMappingExpr)
            && !string.IsNullOrWhiteSpace(r.ResponseMappingExpr)
            && !string.IsNullOrWhiteSpace(r.CandidateFilterName)
            && !string.IsNullOrWhiteSpace(r.ResponseSampleJson)
            && !string.IsNullOrWhiteSpace(r.AckSampleJson));
        rows.Should().OnlyContain(r => !r.PathConfirmed && r.VerifiedAt == null, "attestation is never seeded");
        // StatusIn now serves two configs (accept, reject) — PoAcknowledge was the third.
        rows.Where(r => r.CandidateFilterName == "StatusIn").Should().HaveCount(2)
            .And.OnlyContain(r => r.CandidateFilterParams!.Contains("statuses"));

        // R12 — the three PO_Update configs: one shared real endpoint, a seeded monotonic gate, and the real
        // response envelope rather than the generic OData sample.
        var poUpdate = rows.Where(r => r.TransactionType is OutboxTransactionType.PoAccept
            or OutboxTransactionType.PoReject or OutboxTransactionType.PoNegotiationApprove).ToList();
        poUpdate.Should().HaveCount(3);
        poUpdate.Should().OnlyContain(r => r.EndpointPath == "CustomerApi/LNAPI/PO_Update");
        poUpdate.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.EligibilityGateExpr));
        poUpdate.Should().OnlyContain(r => r.ResponseSampleJson!.Contains("PurchaseOrder"));
        poUpdate.Should().NotContain(r => r.EligibilityGateExpr!.Contains("Negotiated"),
            "\"Negotiated\" is a wire-only POStatus; as a gate it would fail closed on every row");
    }

    [SkippableFact]
    public async Task Hash_gate_matrix_hand_edit_survives_repo_change_flows_to_untouched()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SeedAsync();
        var defaults = new LnDefaultExpressions();
        var repoInvoiceRequest = defaults.TryGet(OutboxTransactionType.InvoicePost)!.RequestExpr;

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Hand-edit InvoicePost's request expression (hash no longer matches its seed hash).
            var invoice = await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.InvoicePost && !c.IsDeleted);
            invoice.RequestMappingExpr = "{ \"HandEdited\": invoiceNumber }";
            // Simulate a pre-repo-change AsnPost row: stored text + seed hash agree with an OLD default.
            var asn = await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.AsnPost && !c.IsDeleted);
            asn.RequestMappingExpr = "{ \"Old\": asnNumber }";
            asn.RequestMappingSeedHash = ExpressionHash.Compute("{ \"Old\": asnNumber }");
            await db.SaveChangesAsync();
        }

        await SeedAsync(); // re-seed

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var invoice = await db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.InvoicePost && !c.IsDeleted);
            invoice.RequestMappingExpr.Should().Be("{ \"HandEdited\": invoiceNumber }", "hand edits are never clobbered — that difference IS the drift flag");

            var asn = await db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.AsnPost && !c.IsDeleted);
            asn.RequestMappingExpr.Should().Be(defaults.TryGet(OutboxTransactionType.AsnPost)!.RequestExpr,
                "an untouched-since-seed row whose repo default changed must flow forward");
        }

        // Restore the tenant's rows to pristine repo state for other tests: hard-reset the two mutated slots.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var invoice = await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.InvoicePost && !c.IsDeleted);
            invoice.RequestMappingExpr = repoInvoiceRequest;
            invoice.RequestMappingSeedHash = defaults.TryGet(OutboxTransactionType.InvoicePost)!.RequestHash;
            await db.SaveChangesAsync();
        }
    }
}
