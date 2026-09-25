using AppPlatform.Api;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Tenants;
using AppPlatform.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Two operators granting the same entitlement at once, against real PostgreSQL.
///
/// Cannot be written against the Moq fixture: the preflight read genuinely cannot see an insert
/// that has not happened yet, so the behaviour only exists when a real unique index is present.
/// Before the fix the loser got an unhandled 500 naming a database constraint.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class EntitlementConcurrencyTests(PrivilegeFixture fixture)
{
    private PlatformDbContext Open()
        => new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(fixture.ConnectionString).UseSnakeCaseNamingConvention().Options);

    private async Task<string> SeedTenantAsync(string name)
    {
        await using var db = Open();
        var tenant = new Tenant
        {
            PublicId = Ids.PublicId.New("ten").ToString(),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.PublicId;
    }

    private async Task<CommandResult> GrantAsync(string tenantPublicId)
    {
        await using var db = Open();
        return await new SetEntitlementFeature.SetEntitlementCommandHandler(
            new EFTenantService(db), TimeProvider.System)
            .Handle(Guid.NewGuid(), tenantPublicId, new("tickets", true));
    }

    [Fact]
    public async Task Two_concurrent_grants_yield_one_success_and_one_conflict()
    {
        var tenantPublicId = await SeedTenantAsync("Race Entitlement Ltd");

        var results = await Task.WhenAll(GrantAsync(tenantPublicId), GrantAsync(tenantPublicId));

        Assert.Equal(1, results.Count(r => r.Succeeded));

        var loser = results.Single(r => !r.Succeeded);
        // A stable conflict the operator can act on, not a 500 naming an index.
        Assert.Equal(CommandOutcome.Conflict, loser.Outcome);
        Assert.Equal(PlatformProblems.AppAlreadyLicensed, loser.ProblemCode);
    }

    [Fact]
    public async Task Only_one_live_grant_exists_after_a_concurrent_race()
    {
        var tenantPublicId = await SeedTenantAsync("Single Grant Ltd");

        await Task.WhenAll(GrantAsync(tenantPublicId), GrantAsync(tenantPublicId));

        await using var db = Open();
        var tenantId = (await db.Tenants.SingleAsync(t => t.PublicId == tenantPublicId)).Id;

        // Two live rows would make "is this licensed" depend on which one a query read. The
        // partial unique index is what guarantees this; the handler only reports it well.
        Assert.Single(await db.TenantApps
            .Where(a => a.TenantId == tenantId && a.RevokedAt == null)
            .ToListAsync());
    }

    [Fact]
    public async Task A_sequential_grant_still_succeeds()
    {
        // Guards the guard: a handler that rejected every grant would also pass the race test,
        // because one of the two would still be the loser.
        Assert.True((await GrantAsync(await SeedTenantAsync("Sequential Ltd"))).Succeeded);
    }
}
