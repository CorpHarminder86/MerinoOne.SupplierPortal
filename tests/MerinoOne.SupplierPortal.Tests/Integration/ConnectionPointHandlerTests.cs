using FluentAssertions;
using FluentValidation;
using MerinoOne.SupplierPortal.Application.Integration.Connection;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R10 — connection-point handlers against the real DB. The list handler is the regression anchor: its
/// first shape used a correlated Count subquery with IgnoreQueryFilters inside the projection, which EF
/// cannot translate — the endpoint 500'd at runtime while everything compiled.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ConnectionPointHandlerTests
{
    private readonly IntegrationTestFixture _fx;
    public ConnectionPointHandlerTests(IntegrationTestFixture fx) => _fx = fx;

    private sealed class StubUser(Guid tenantId) : Application.Common.Interfaces.ICurrentUser
    {
        public string UserCode => "test:cp";
        public string? UserName => "test:cp";
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public IReadOnlyCollection<string> Permissions => Array.Empty<string>();
        public bool IsAuthenticated => true;
        public bool IsManager => false;
        public bool IsAdmin => true;
        public bool HasPermission(string code) => true;
        public Guid? TenantId { get; } = tenantId;
        public bool IsPlatformAdmin => false;
    }

    [SkippableFact]
    public async Task List_translates_and_reports_in_use_counts_and_transport_flags()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var tenantId = Guid.NewGuid();   // instance tenant — no cross-run state
        Guid ionId;

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ion = new ConnectionPoint
            {
                TenantId = tenantId, Name = "Default — Infor ION",
                SystemType = ConnectionSystemTypes.InforIon, IsDefault = true, CreatedBy = "seed",
            };
            var tally = new ConnectionPoint
            {
                TenantId = tenantId, Name = "Tally — test",
                SystemType = ConnectionSystemTypes.Tally, BaseUrl = "https://tally.local:9000", CreatedBy = "seed",
            };
            db.ConnectionPoints.AddRange(ion, tally);
            db.OutboundIntegrationConfigs.Add(new Domain.Entities.Integration.OutboundIntegrationConfig
            {
                TenantId = tenantId,
                Kind = Domain.Enums.OutboundIntegrationKind.Transaction,
                TransactionType = $"CpTest-{tenantId:N}"[..20],
                PortalEntity = "Invoice",
                EndpointPath = "/x",
                RequestMappingExpr = "{}",
                ConnectionPoint = ion,
                CreatedBy = "seed",
            });
            await db.SaveChangesAsync();
            ionId = ion.Id;
        }

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var handler = new GetConnectionPointsQueryHandler(db, new StubUser(tenantId));
            var list = await handler.Handle(new GetConnectionPointsQuery(), CancellationToken.None);

            list.Should().HaveCount(2);
            var ion = list.Single(p => p.SystemType == ConnectionSystemTypes.InforIon);
            ion.IsDefault.Should().BeTrue();
            ion.InUseCount.Should().Be(1, because: "one config row tags the ION connection");
            ion.TransportAvailable.Should().BeTrue();
            var tally = list.Single(p => p.SystemType == ConnectionSystemTypes.Tally);
            tally.InUseCount.Should().Be(0);
            tally.TransportAvailable.Should().BeFalse(because: "no Tally transport is registered yet");
        }
    }

    [SkippableFact]
    public async Task Delete_blocked_while_default_or_in_use_and_setdefault_blocked_without_transport()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var tenantId = Guid.NewGuid();
        Guid ionId, tallyId;

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ion = new ConnectionPoint
            {
                TenantId = tenantId, Name = "ION", SystemType = ConnectionSystemTypes.InforIon,
                IsDefault = true, CreatedBy = "seed",
            };
            var tally = new ConnectionPoint
            {
                TenantId = tenantId, Name = "Tally", SystemType = ConnectionSystemTypes.Tally,
                BaseUrl = "https://tally.local:9000", CreatedBy = "seed",
            };
            db.ConnectionPoints.AddRange(ion, tally);
            await db.SaveChangesAsync();
            ionId = ion.Id;
            tallyId = tally.Id;
        }

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = new StubUser(tenantId);

            // Default cannot be deleted.
            await Assert.ThrowsAsync<ValidationException>(() =>
                new DeleteConnectionPointCommandHandler(db, user)
                    .Handle(new DeleteConnectionPointCommand(ionId), CancellationToken.None));

            // A transport-less type cannot become the default.
            await Assert.ThrowsAsync<ValidationException>(() =>
                new SetDefaultConnectionPointCommandHandler(db, user)
                    .Handle(new SetDefaultConnectionPointCommand(tallyId), CancellationToken.None));

            // The unused non-default row deletes fine.
            var deleted = await new DeleteConnectionPointCommandHandler(db, user)
                .Handle(new DeleteConnectionPointCommand(tallyId), CancellationToken.None);
            deleted.Should().BeTrue();
        }
    }
}
