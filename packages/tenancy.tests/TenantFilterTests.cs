using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tenancy.Tests;

public class TenantFilterTests
{
    [Fact]
    public async Task Tenant_B_cannot_see_tenant_A_rows()
    {
        const string db = nameof(Tenant_B_cannot_see_tenant_A_rows);

        var (a, _) = Harness.Context(db, tenantId: 1);
        a.Widgets.Add(new Widget { Name = "tenant one" });
        await a.SaveChangesAsync();

        var (b, _) = Harness.Context(db, tenantId: 2);
        Assert.Empty(await b.Widgets.ToListAsync());
        Assert.Null(await b.Widgets.FirstOrDefaultAsync(w => w.Name == "tenant one"));
        Assert.Equal(0, await b.Widgets.CountAsync());
    }

    [Fact]
    public async Task No_tenant_context_returns_nothing_rather_than_everything()
    {
        const string db = nameof(No_tenant_context_returns_nothing_rather_than_everything);

        var (seed, _) = Harness.Context(db, tenantId: 1);
        seed.Widgets.Add(new Widget { Name = "one" });
        await seed.SaveChangesAsync();

        // The failure mode this guards: a filter written as `tenantId == null || match`
        // would hand an anonymous endpoint every tenant's data at once.
        var (anon, _) = Harness.Context(db);
        Assert.Empty(await anon.Widgets.ToListAsync());
    }

    [Fact]
    public async Task Entities_that_are_not_tenant_scoped_are_untouched()
    {
        const string db = nameof(Entities_that_are_not_tenant_scoped_are_untouched);

        var (seed, _) = Harness.Context(db, tenantId: 1);
        seed.Regions.Add(new Region { Id = 1, Name = "north" });
        await seed.SaveChangesAsync();

        var (other, _) = Harness.Context(db, tenantId: 2);
        Assert.Single(await other.Regions.ToListAsync());
    }

    [Fact]
    public async Task Filter_follows_the_provider_within_one_context()
    {
        const string db = nameof(Filter_follows_the_provider_within_one_context);

        var (seed, _) = Harness.Context(db, tenantId: 1);
        seed.Widgets.Add(new Widget { Name = "one" });
        await seed.SaveChangesAsync();

        // Proves the filter reads the provider per query rather than capturing its value
        // when the model was built — the bug that serves request one's tenant to everyone.
        var (db2, provider) = Harness.Context(db, tenantId: 2);
        Assert.Empty(await db2.Widgets.ToListAsync());

        provider.UseTenant(1);
        Assert.Single(await db2.Widgets.ToListAsync());
    }
}
