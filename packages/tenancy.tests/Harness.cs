using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tenancy.Tests;

internal static class Harness
{
    /// <summary>
    /// One in-memory database name per test, so tests cannot see each other's rows and a
    /// leak here is a bug in the code under test rather than in the harness.
    /// </summary>
    public static (TestDbContext Db, AmbientTenantProvider Tenant) Context(string name, int? tenantId = null)
    {
        var provider = new AmbientTenantProvider();
        if (tenantId is { } id) provider.UseTenant(id);

        var options = new DbContextOptionsBuilder()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics
                .InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return (new TestDbContext(options, provider), provider);
    }
}
