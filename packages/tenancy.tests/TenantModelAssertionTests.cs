using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.Tenancy.Tests;

public class TenantModelAssertionTests
{
    private static IModel ModelOf<T>() where T : TenantedDbContext
    {
        var provider = new AmbientTenantProvider();
        var options = new DbContextOptionsBuilder().UseInMemoryDatabase(typeof(T).Name).Options;
        var db = (T)Activator.CreateInstance(typeof(T), options, provider)!;
        return db.Model;
    }

    [Fact]
    public void Good_model_carries_tenant_on_every_reference()
        => Assert.Empty(TenantModelAssertions.FindReferencesNotCarryingTenant(ModelOf<TestDbContext>()));

    [Fact]
    public void Good_model_indexes_every_tenant_scoped_entity()
        => Assert.Empty(TenantModelAssertions.FindEntitiesMissingTenantIndex(ModelOf<TestDbContext>()));

    [Fact]
    public void Tenant_blind_reference_is_reported()
    {
        // The assertion is only worth having if it FAILS on a bad model. A check that
        // has never been seen to fail is a check nobody knows is wired up.
        var problems = TenantModelAssertions.FindReferencesNotCarryingTenant(ModelOf<BadModelDbContext>());

        Assert.Contains(problems, p =>
            p.Contains("Gadget.WidgetId", StringComparison.Ordinal)
            && p.Contains("does not carry TenantId", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_tenant_index_is_reported()
    {
        var problems = TenantModelAssertions.FindEntitiesMissingTenantIndex(ModelOf<BadModelDbContext>());

        Assert.Contains("Widget", problems);
        Assert.Contains("Gadget", problems);
    }
}
