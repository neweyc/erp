using AppPlatform.Platform.Data;
using AppPlatform.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The queries behind the operator console, against real PostgreSQL and as the platform's own
/// runtime role. They project enums, nested lists and correlated lookups, which an in-memory
/// provider would happily evaluate and PostgreSQL might not translate; and a missing grant would
/// otherwise surface only in the browser.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class OperatorConsoleReadTests(PrivilegeFixture fixture)
{
    private PlatformDbContext Open(string? role = "ap_platform_rt")
        => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(role is null ? fixture.ConnectionString : fixture.ConnectionStringAs(role))
            .UseSnakeCaseNamingConvention().Options);

    private async Task<Tenant> SeedTenantAsync(string name, TenantStatus status = TenantStatus.Active)
    {
        await using var db = Open(role: null);
        var tenant = new Tenant
        {
            PublicId = Ids.PublicId.New("ten").ToString(),
            Name = name,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    [Fact]
    public async Task The_tenant_list_carries_status_and_only_live_apps()
    {
        var tenant = await SeedTenantAsync("Console List Ltd " + Guid.NewGuid(), TenantStatus.Suspended);
        await using (var seed = Open(role: null))
        {
            seed.TenantApps.AddRange(
                new TenantApp { TenantId = tenant.Id, App = "tickets", GrantedAt = DateTimeOffset.UtcNow },
                new TenantApp
                {
                    TenantId = tenant.Id, App = "ledger",
                    GrantedAt = DateTimeOffset.UtcNow.AddDays(-2), RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
                });
            await seed.SaveChangesAsync();
        }

        await using var db = Open();
        var service = new EFTenantService(db);

        var row = Assert.Single(await service.ListSummariesAsync(), t => t.TenantId == tenant.PublicId);
        Assert.Equal("Suspended", row.Status);
        Assert.Equal(["tickets"], row.Apps);

        var history = await service.GrantHistoryAsync(tenant.Id);
        Assert.Equal(["tickets", "ledger"], history.Select(g => g.App));
        Assert.NotNull(history[1].RevokedAt);
    }

    [Fact]
    public async Task The_audit_log_names_the_operator_and_filters_by_tenant()
    {
        var tenant = await SeedTenantAsync("Console Audit Ltd " + Guid.NewGuid());
        var operatorId = Guid.NewGuid();
        var email = $"op-{operatorId:N}@console.test";

        await using (var seed = Open(role: null))
        {
            seed.PlatformUsers.Add(new PlatformUser
            {
                Id = operatorId, PublicId = Ids.PublicId.New("op").ToString(), Email = email, PasswordHash = "x",
            });
            seed.AuditLogs.AddRange(
                new PlatformAuditLog
                {
                    PlatformUserId = operatorId, Action = "tenant.suspended",
                    TenantPublicId = tenant.PublicId, CreatedAt = DateTimeOffset.UtcNow,
                },
                // A failed sign-in has no operator; it must still be listed, with nobody named.
                new PlatformAuditLog { Action = "operator.sign_in_failed", Detail = "x", CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using var db = Open();
        var audit = new EFAuditLogService(db);

        var forTenant = await audit.RecentAsync(tenant.PublicId, 10);
        var entry = Assert.Single(forTenant);
        Assert.Equal(email, entry.OperatorEmail);
        Assert.Equal("tenant.suspended", entry.Action);

        var everything = await audit.RecentAsync(null, 500);
        Assert.Contains(everything, a => a.Action == "operator.sign_in_failed" && a.OperatorEmail is null);
    }

    [Fact]
    public async Task A_tenant_created_twice_under_one_key_is_audited_once()
    {
        // Two retries of one creation race to record it. The loser's audit entry must go with its
        // idempotency record, or one tenant would appear in the log as created twice.
        var key = "console-" + Guid.NewGuid();
        var tenantId = Ids.PublicId.New("ten").ToString();

        async Task<bool> RecordAsync()
        {
            await using var db = Open();
            return await new EFIdempotencyService(db).TryRecordAsync(
                "create_tenant", key, $$"""{"tenantId":"{{tenantId}}"}""",
                new PlatformAuditLog { Action = "tenant.created", TenantPublicId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        }

        var results = await Task.WhenAll(RecordAsync(), RecordAsync());

        Assert.Equal(1, results.Count(recorded => recorded));
        await using var check = Open(role: null);
        Assert.Equal(1, await check.AuditLogs.CountAsync(a => a.TenantPublicId == tenantId));
    }
}
