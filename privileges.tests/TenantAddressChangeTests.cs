using AppPlatform.Platform.Data;
using AppPlatform.Platform.Services;
using Microsoft.EntityFrameworkCore;
using static AppPlatform.Platform.Features.Tenants.ChangeTenantAddressFeature;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Renaming a tenant's address against real PostgreSQL, as the platform's runtime role. The
/// partial unique index (one current address per tenant) and the address's primary key are what
/// decide a race, and only a real database enforces them.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class TenantAddressChangeTests(PrivilegeFixture fixture)
{
    private PlatformDbContext Open(string? role = "ap_platform_rt")
        => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(role is null ? fixture.ConnectionString : fixture.ConnectionStringAs(role))
            .UseSnakeCaseNamingConvention().Options);

    private static string Fresh(string stem) => $"{stem}-{Guid.NewGuid():N}"[..20];

    private async Task<Tenant> SeedTenantAsync(string subdomain)
    {
        await using var db = Open(role: null);
        var tenant = new Tenant
        {
            PublicId = Ids.PublicId.New("ten").ToString(),
            Name = "Address " + subdomain,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        db.TenantSubdomains.Add(new TenantSubdomain { Subdomain = subdomain, TenantId = tenant.Id, AssignedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<Api.CommandResult> ChangeAsync(Tenant tenant, string subdomain)
    {
        await using var db = Open();
        return await new ChangeTenantAddressCommandHandler(new EFTenantService(db), TimeProvider.System)
            .Handle(Guid.NewGuid(), tenant.PublicId, new(subdomain));
    }

    private async Task<List<TenantSubdomain>> AddressesAsync(Tenant tenant)
    {
        await using var db = Open(role: null);
        return await db.TenantSubdomains.Where(s => s.TenantId == tenant.Id).ToListAsync();
    }

    [Fact]
    public async Task A_rename_keeps_the_old_address_as_a_former_one_and_audits_it()
    {
        var old = Fresh("old");
        var tenant = await SeedTenantAsync(old);
        var renamed = Fresh("new");

        var result = await ChangeAsync(tenant, renamed);

        Assert.True(result.Succeeded, result.Message);
        var addresses = await AddressesAsync(tenant);
        Assert.Equal(renamed, Assert.Single(addresses, a => a.ReplacedAt is null).Subdomain);
        Assert.NotNull(Assert.Single(addresses, a => a.Subdomain == old).ReplacedAt);

        await using var db = Open(role: null);
        var entry = await db.AuditLogs.SingleAsync(a => a.TenantPublicId == tenant.PublicId);
        Assert.Equal($"{old} -> {renamed}", entry.Detail);
    }

    [Fact]
    public async Task Concurrent_renames_of_one_tenant_leave_exactly_one_current_address()
    {
        var tenant = await SeedTenantAsync(Fresh("start"));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => ChangeAsync(tenant, Fresh($"race{i}"))));

        Assert.Contains(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(Platform.Features.Tenants.PlatformProblems.SubdomainTaken, r.ProblemCode));
        Assert.Single(await AddressesAsync(tenant), a => a.ReplacedAt is null);
    }

    [Fact]
    public async Task Two_tenants_racing_for_one_address_cannot_both_have_it()
    {
        var first = await SeedTenantAsync(Fresh("first"));
        var second = await SeedTenantAsync(Fresh("second"));
        var contested = Fresh("contested");

        var results = await Task.WhenAll(ChangeAsync(first, contested), ChangeAsync(second, contested));

        Assert.Single(results, r => r.Succeeded);
        await using var db = Open(role: null);
        Assert.Single(await db.TenantSubdomains.Where(s => s.Subdomain == contested).ToListAsync());
        // The loser keeps the address it had.
        var loser = results[0].Succeeded ? second : first;
        Assert.Single(await AddressesAsync(loser), a => a.ReplacedAt is null);
    }

    [Fact]
    public async Task An_address_another_tenant_once_had_is_never_given_out_again()
    {
        var original = Fresh("orig");
        var holder = await SeedTenantAsync(original);
        Assert.True((await ChangeAsync(holder, Fresh("moved"))).Succeeded);
        var other = await SeedTenantAsync(Fresh("other"));

        var result = await ChangeAsync(other, original);

        Assert.Equal(Platform.Features.Tenants.PlatformProblems.SubdomainTaken, result.ProblemCode);
    }

    [Fact]
    public async Task The_list_and_the_history_read_addresses_as_the_platform_role()
    {
        var first = Fresh("hist");
        var tenant = await SeedTenantAsync(first);
        var second = Fresh("hist");
        Assert.True((await ChangeAsync(tenant, second)).Succeeded);

        await using var db = Open();
        var service = new EFTenantService(db);

        Assert.Equal(second, Assert.Single(await service.ListSummariesAsync(), t => t.TenantId == tenant.PublicId).Subdomain);
        Assert.Equal([second, first], (await service.AddressHistoryAsync(tenant.Id)).Select(a => a.Subdomain));
    }
}
