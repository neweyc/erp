using AppPlatform.Audit;
using AppPlatform.Core.Data;
using AppPlatform.Platform.Data;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// <c>platform.tenant</c> and <c>platform.tenant_subdomain</c> are mapped by BOTH services: platform
/// owns their DDL, core maps a twin of each so provisioning can insert a row. Nothing in the
/// compiler relates the two.
///
/// This exists because the omission it catches is invisible until production. Core's twin was
/// missing <c>created_at</c> — not-null in the real schema with no default — so every unit test
/// passed against a mocked service, and provisioning would have failed on its first real INSERT
/// with a not-null violation.
/// </summary>
public class TwinParityTests
{
    private static CoreDbContext CoreContext()
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(1);

        return new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql("Host=unused").UseSnakeCaseNamingConvention().Options,
            tenant, new AmbientAuditActor(), TimeProvider.System);
    }

    private static IModel CoreModel() => CoreContext().Model;

    private static IModel PlatformModel()
        => new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNpgsql("Host=unused").UseSnakeCaseNamingConvention().Options).Model;

    private static Dictionary<string, (string Type, bool Nullable)> Columns(IModel model, string table)
        => model.GetEntityTypes()
            .Single(e => e.GetTableName() == table && e.GetSchema() == "platform")
            .GetProperties()
            .ToDictionary(
                p => p.GetColumnName(),
                p => (p.GetColumnType(), p.IsNullable));

    [Theory]
    [InlineData("tenant")]
    [InlineData("tenant_subdomain")]
    public void Core_maps_every_column_platform_requires(string table)
    {
        var platform = Columns(PlatformModel(), table);
        var core = Columns(CoreModel(), table);

        // Only the REQUIRED columns matter in this direction: core may legitimately omit an
        // optional one it never reads, but a required column it cannot supply is an INSERT that
        // always fails.
        var missing = platform
            .Where(c => !c.Value.Nullable && !core.ContainsKey(c.Key))
            .Select(c => c.Key)
            .Order()
            .ToArray();

        Assert.True(missing.Length == 0,
            $"core's {table} twin is missing required column(s): {string.Join(", ", missing)}. " +
            "Provisioning would fail with a not-null violation on the first INSERT.");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("tenant_subdomain")]
    public void Shared_columns_agree_on_type_and_nullability(string table)
    {
        var platform = Columns(PlatformModel(), table);
        var core = Columns(CoreModel(), table);

        var mismatches = core
            .Where(c => platform.ContainsKey(c.Key))
            .Where(c => platform[c.Key] != c.Value)
            .Select(c => $"{c.Key}: platform={platform[c.Key]} core={c.Value}")
            .Order()
            .ToArray();

        Assert.True(mismatches.Length == 0, string.Join("; ", mismatches));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("tenant_subdomain")]
    public void Core_does_not_migrate_the_tables_platform_owns(string table)
    {
        // The DESIGN-TIME model, not the runtime one: EF strips the exclusion from the
        // read-optimized model and throws if you ask, which is what a migration is generated
        // from anyway — so this is the model whose answer actually matters.
        var twin = CoreContext().GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Single(e => e.GetTableName() == table && e.GetSchema() == "platform");

        // Without the exclusion, core's migration emits CREATE TABLE platform.<table>, colliding
        // with platform's own and failing anyway for want of DDL rights on that schema.
        Assert.True(twin.IsTableExcludedFromMigrations());
    }
}
