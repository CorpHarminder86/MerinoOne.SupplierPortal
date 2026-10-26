using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// Seeds the default tenant topology for local/dev:
///   - Tenant <c>Merino</c> (isActive).
///   - TenantEntities (companies) 2000, 3000, 4000, 5000, 6000, 7000.
///   - A Merino Tenant Admin user (Admin role; MustChangePassword).
///   - Share groups for PaymentTerm + DeliveryTerm: source 2000 → {2000,3000,4000};
///     source 5000 → {5000,6000}; 7000 standalone (no group).
///   - Inbound InforEndpointMap rows for PaymentTerm + DeliveryTerm (Direction=Inbound, IsEnabled=true).
///   - The default EmailTemplate set cloned into Merino so the tenant starts with editable templates.
///
/// All ids are deterministic (matches MasterSeeder style) so reruns are idempotent. Every row stamps
/// TenantId / TenantEntityId EXPLICITLY (never relies on the stamp interceptor under the seed principal).
/// </summary>
public static class TenantSeeder
{
    public const string TenantName = "Merino";
    public const string AdminUserCode = "merinoadmin";
    public const string AdminEmail = "merinoadmin@merino.local";
    public const string AdminPassword = "Merino@123";

    public static readonly string[] CompanyCodes = { "2000", "3000", "4000", "5000", "6000", "7000" };

    // Share-group topology per endpoint: source → members. 7000 is standalone (absent here).
    private static readonly (string Source, string[] Members)[] ShareTopology =
    {
        ("2000", new[] { "2000", "3000", "4000" }),
        ("5000", new[] { "5000", "6000" }),
    };

    // Inbound endpoint EntityName → route segment under /api/integration/inbound. Covers both the
    // company-scoped (SharedEndpoint) and tenant-scoped (TenantInboundEntity) masters.
    private static readonly (string EntityName, string Route)[] InboundRoutes =
    {
        (nameof(SharedEndpoint.PaymentTerm),       "payment-terms"),
        (nameof(SharedEndpoint.DeliveryTerm),      "delivery-terms"),
        (nameof(SharedEndpoint.Carrier),           "carriers"),
        (nameof(SharedEndpoint.Unit),              "units"),
        (nameof(SharedEndpoint.ItemGroup),         "item-groups"),
        (nameof(SharedEndpoint.Item),              "items"),
        (nameof(SharedEndpoint.Tax),               "taxes"),
        (nameof(TenantInboundEntity.Currency),     "currencies"),
        (nameof(TenantInboundEntity.Country),      "countries"),
        (nameof(TenantInboundEntity.State),        "states"),
        (nameof(TenantInboundEntity.City),         "cities"),
        (nameof(TenantInboundEntity.PostalCode),   "postal-codes"),
        // R4 Module 5 / Increment D — the transactional ERP inbound loop. Direction=Inbound, IsEnabled=true
        // (kill-switch). Idempotent — re-running skips entity names already present for the tenant.
        (nameof(TransactionalInboundEntity.Grn),           "grn-status"),
        (nameof(TransactionalInboundEntity.Payment),       "payments"),
        (nameof(TransactionalInboundEntity.InvoiceStatus), "invoice-status"),
        (nameof(TransactionalInboundEntity.ErpAck),        "erp-ack"),
        // R4 (2026-06-23) — transactional document ingestion (create/upsert).
        (nameof(TransactionalInboundEntity.Po),              "purchase-orders"),
        (nameof(TransactionalInboundEntity.DeliverySchedule),"delivery-schedules"),
        (nameof(TransactionalInboundEntity.GrnReceipt),      "goods-receipts"),
        // R17 (2026-10-26) — LN-pushed ASN shipment status + received quantities.
        (nameof(TransactionalInboundEntity.AsnUpdate),       "asn-update"),
    };

    public static Guid TenantId => DeterministicId.From("Tenant", TenantName);
    public static Guid CompanyId(string code) => DeterministicId.From("TenantEntity", $"{TenantName}|{code}");

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tenantId = TenantId;

        // 1. Tenant ---------------------------------------------------------------------------------
        if (!await ctx.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId, ct))
        {
            ctx.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = TenantName,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            });
            await ctx.SaveChangesAsync(ct);
        }

        // 2. Companies (TenantEntities) -------------------------------------------------------------
        var existingCompanyIds = await ctx.TenantEntities.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .Select(e => e.Id)
            .ToListAsync(ct);

        foreach (var code in CompanyCodes)
        {
            var id = CompanyId(code);
            if (existingCompanyIds.Contains(id)) continue;
            ctx.TenantEntities.Add(new TenantEntity
            {
                Id = id,
                TenantId = tenantId,
                Code = code,
                Name = $"Merino Company {code}",
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = now
            });
        }
        await ctx.SaveChangesAsync(ct);

        // 3. Tenant Admin user ----------------------------------------------------------------------
        await SeedTenantAdminAsync(ctx, tenantId, now, ct);

        // 4. Share groups + members (PaymentTerm + DeliveryTerm) ------------------------------------
        await SeedShareGroupsAsync(ctx, tenantId, now, ct);

        // 5. Inbound endpoint maps ------------------------------------------------------------------
        await SeedInboundEndpointMapsAsync(ctx, tenantId, now, ct);

        // 6. Per-tenant default EmailTemplate set ---------------------------------------------------
        await SeedTenantEmailTemplatesAsync(ctx, tenantId, now, ct);
    }

    private static async Task SeedTenantAdminAsync(AppDbContext ctx, Guid tenantId, DateTime now, CancellationToken ct)
    {
        var userId = DeterministicId.From("AppUser", AdminUserCode);
        if (await ctx.AppUsers.IgnoreQueryFilters().AnyAsync(u => u.Id == userId, ct))
            return;

        var adminRole = await ctx.Roles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => !r.IsDeleted && r.Name == RoleNames.Admin
                                      && (r.TenantId == tenantId || r.TenantId == null), ct);

        var seccodeId = DeterministicId.From("Seccode.U", AdminUserCode);

        ctx.AppUsers.Add(new AppUser
        {
            Id = userId,
            TenantId = tenantId,
            UserCode = AdminUserCode,
            FullName = "Merino Tenant Admin",
            Email = AdminEmail,
            PasswordHash = PasswordHasher.DeterministicHash(AdminPassword),
            IsInternal = true,
            IsMfaEnabled = false,
            IsActive = true,
            MustChangePassword = true,
            CreatedBy = "seed",
            CreatedOn = now
        });

        if (adminRole is not null)
        {
            ctx.UserRoles.Add(new UserRole
            {
                Id = DeterministicId.From("UserRole", $"{AdminUserCode}|Admin"),
                AppUserId = userId,
                RoleId = adminRole.Id,
                CreatedBy = "seed",
                CreatedOn = now
            });
        }

        ctx.Seccodes.Add(new Seccode
        {
            Id = seccodeId,
            SeccodeType = SeccodeType.U,
            Name = AdminUserCode + " default",
            AppUserId = userId,
            TenantId = tenantId,
            CreatedBy = "seed",
            CreatedOn = now
        });
        ctx.SecRights.Add(new SecRight
        {
            Id = DeterministicId.From("SecRight.U", AdminUserCode),
            SeccodeId = seccodeId,
            UserCode = AdminUserCode,
            CanRead = true,
            CanWrite = true,
            CreatedBy = "seed",
            CreatedOn = now
        });

        // Tenant Admin is implicit-all on companies (no UserCompanyMap rows needed).
        await ctx.SaveChangesAsync(ct);
    }

    private static async Task SeedShareGroupsAsync(AppDbContext ctx, Guid tenantId, DateTime now, CancellationToken ct)
    {
        // Company-scoped sharing participants (PaymentTerm/DeliveryTerm + the new inventory masters).
        // Tenant-scoped masters (Currency/Country/State/City/PostalCode) do NOT share company-wise.
        var endpoints = new[]
        {
            SharedEndpoint.PaymentTerm, SharedEndpoint.DeliveryTerm, SharedEndpoint.Carrier,
            SharedEndpoint.Unit, SharedEndpoint.ItemGroup, SharedEndpoint.Item
        };

        // R17 (2026-10-26) — the deterministic Ids alone are NOT a sufficient idempotency key. Adding
        // SharedEndpoint.Carrier to the list above made `seed` insert Carrier groups for a tenant where an admin had
        // ALREADY created a Carrier group for the same source company through the Share Groups UI (a random Guid Id);
        // the Id check missed it and the unique indexes rejected the insert. So the seeder checks the NATURAL keys the
        // indexes actually enforce, matching each index's filter exactly, and respects whatever an admin configured
        // (an admin-created or admin-deleted group for a source is left completely alone — no insert, no members added):
        //   UQ_CompanyShareGroup_tenant_endpoint_source   (TenantId, Endpoint, SourceTenantEntityId) — NOT filtered →
        //                                                 soft-deleted groups still block, so they are loaded too.
        //   UQ_CompanyShareGroupMember_group_member       (CompanyShareGroupId, MemberTenantEntityId) — NOT filtered.
        //   UQ_CompanyShareGroupMember_endpoint_member    (Endpoint, MemberTenantEntityId) WHERE isDeleted = 0 → a company
        //                                                 sits in at most ONE LIVE group per endpoint.
        // Everything is loaded once and every row added in this pass is folded back into the sets, so two topology
        // entries can never collide inside the single SaveChanges below.
        var groups = await ctx.CompanyShareGroups.IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId)
            .Select(g => new { g.Id, g.Endpoint, g.SourceTenantEntityId, g.IsDeleted })
            .ToListAsync(ct);
        var groupIds = groups.Select(g => g.Id).ToHashSet();
        var groupByNaturalKey = groups.ToDictionary(g => (g.Endpoint, g.SourceTenantEntityId), g => (g.Id, g.IsDeleted));

        // Members are bounded by the tenant's own companies — both member indexes key on the member company, so this is
        // exactly the set a seeded row could collide with (and it does not depend on a hand-made row carrying a TenantId).
        var companyIds = CompanyCodes.Select(CompanyId).ToList();
        var members = await ctx.CompanyShareGroupMembers.IgnoreQueryFilters()
            .Where(m => companyIds.Contains(m.MemberTenantEntityId))
            .Select(m => new { m.Id, m.CompanyShareGroupId, m.MemberTenantEntityId, m.Endpoint, m.IsDeleted })
            .ToListAsync(ct);
        var memberIds = members.Select(m => m.Id).ToHashSet();
        var groupMemberKeys = members.Select(m => (m.CompanyShareGroupId, m.MemberTenantEntityId)).ToHashSet();
        var liveEndpointMembers = members.Where(m => !m.IsDeleted).Select(m => (m.Endpoint, m.MemberTenantEntityId)).ToHashSet();

        foreach (var endpoint in endpoints)
        {
            foreach (var (sourceCode, memberCodes) in ShareTopology)
            {
                var sourceId = CompanyId(sourceCode);
                var deterministicGroupId = DeterministicId.From("CompanyShareGroup", $"{TenantName}|{endpoint}|{sourceCode}");

                Guid groupId;
                if (groupByNaturalKey.TryGetValue((endpoint, sourceId), out var found))
                {
                    // A group for this (tenant, endpoint, source) already exists, whoever created it. An admin who
                    // deleted it did so deliberately — leave this source's topology for this endpoint alone. A live group
                    // that is NOT the seeder's own (its Id is not the deterministic one) was built by an admin: it is
                    // theirs, so the seeder neither duplicates it nor adds members to it.
                    if (found.IsDeleted || found.Id != deterministicGroupId) continue;
                    groupId = found.Id;
                }
                else if (groupIds.Contains(deterministicGroupId))
                {
                    // The deterministic Id is taken by a group an admin has since re-pointed at another source; adding
                    // here would violate the primary key. Respect their topology.
                    continue;
                }
                else
                {
                    groupId = deterministicGroupId;
                    ctx.CompanyShareGroups.Add(new CompanyShareGroup
                    {
                        Id = groupId,
                        TenantId = tenantId,
                        Endpoint = endpoint,
                        SourceTenantEntityId = sourceId,
                        Name = $"{endpoint} share — source {sourceCode}",
                        IsEnabled = true,
                        CreatedBy = "seed",
                        CreatedOn = now
                    });
                    groupIds.Add(groupId);
                    groupByNaturalKey[(endpoint, sourceId)] = (groupId, false);
                }

                foreach (var memberCode in memberCodes)
                {
                    var memberCompanyId = CompanyId(memberCode);
                    var memberId = DeterministicId.From("CompanyShareGroupMember", $"{TenantName}|{endpoint}|{sourceCode}|{memberCode}");
                    if (memberIds.Contains(memberId)) continue;
                    // Already a member of this group (live or soft-deleted: group_member is unfiltered), or already in a
                    // different LIVE group for this endpoint (endpoint_member) — either way the admin's choice stands.
                    if (groupMemberKeys.Contains((groupId, memberCompanyId))) continue;
                    if (liveEndpointMembers.Contains((endpoint, memberCompanyId))) continue;

                    ctx.CompanyShareGroupMembers.Add(new CompanyShareGroupMember
                    {
                        Id = memberId,
                        TenantId = tenantId,
                        CompanyShareGroupId = groupId,
                        MemberTenantEntityId = memberCompanyId,
                        Endpoint = endpoint,
                        CreatedBy = "seed",
                        CreatedOn = now
                    });
                    memberIds.Add(memberId);
                    groupMemberKeys.Add((groupId, memberCompanyId));
                    liveEndpointMembers.Add((endpoint, memberCompanyId));
                }
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    private static async Task SeedInboundEndpointMapsAsync(AppDbContext ctx, Guid tenantId, DateTime now, CancellationToken ct)
    {
        var existing = await ctx.InforEndpointMaps.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.Direction == SyncDirection.Inbound)
            .Select(m => m.EntityName)
            .ToListAsync(ct);

        foreach (var (entityName, route) in InboundRoutes)
        {
            if (existing.Contains(entityName)) continue;
            ctx.InforEndpointMaps.Add(new InforEndpointMap
            {
                Id = DeterministicId.From("InforEndpointMap", $"{TenantName}|Inbound|{entityName}"),
                TenantId = tenantId,
                EntityName = entityName,
                Direction = SyncDirection.Inbound,
                InforEndpointUrl = $"/api/integration/inbound/{route}",
                BodName = $"Sync{entityName}",
                IsEnabled = true,
                ReceivedCount = 0,
                CreatedBy = "seed",
                CreatedOn = now
            });
        }
        await ctx.SaveChangesAsync(ct);
    }

    private static async Task SeedTenantEmailTemplatesAsync(AppDbContext ctx, Guid tenantId, DateTime now, CancellationToken ct)
    {
        // If Merino already has its own template set, nothing to do.
        if (await ctx.EmailTemplates.IgnoreQueryFilters().AnyAsync(t => t.TenantId == tenantId, ct))
            return;

        foreach (var spec in EmailTemplateSeeder.Specs)
        {
            ctx.EmailTemplates.Add(new EmailTemplate
            {
                Id = DeterministicId.From("EmailTemplate", $"{TenantName}|{spec.TemplateKey}"),
                TenantId = tenantId,
                TemplateKey = spec.TemplateKey,
                Subject = spec.Subject,
                HtmlBody = spec.HtmlBody,
                IsActive = true,
                Notes = spec.Notes,
                CreatedBy = "seed",
                CreatedOn = now
            });
        }
        await ctx.SaveChangesAsync(ct);
    }
}
