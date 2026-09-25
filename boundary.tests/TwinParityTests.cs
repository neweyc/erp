using AppPlatform.Audit;
using AppPlatform.Core.Data;
using AppPlatform.Platform.Data;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AppPlatform.BoundaryTests;

/// <summary>
/// <c>platform.tenant</c> is mapped by BOTH services: platform owns its DDL, core maps a twin so
/// provisioning can insert a row. Nothing in the compiler relates the two.
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

    private static Dictionary<string, (string Type, bool Nullable)> TenantColumns(IModel model)
        => model.GetEntityTypes()
            .Single(e => e.GetTableName() == "tenant" && e.GetSchema() == "platform")
            .GetProperties()
            .ToDictionary(
                p => p.GetColumnName(),
                p => (p.GetColumnType(), p.IsNullable));

    [Fact]
    public void Core_maps_every_column_platform_requires()
    {
        var platform = TenantColumns(PlatformModel());
        var core = TenantColumns(CoreModel());

        // Only the REQUIRED columns matter in this direction: core may legitimately omit an
        // optional one it never reads, but a required column it cannot supply is an INSERT that
        // always fails.
        var missing = platform
            .Where(c => !c.Value.Nullable && !core.ContainsKey(c.Key))
            .Select(c => c.Key)
            .Order()
            .ToArray();

        Assert.True(missing.Length == 0,
            $"core's tenant twin is missing required column(s): {string.Join(", ", missing)}. " +
            "Provisioning would fail with a not-null violation on the first INSERT.");
    }

    [Fact]
    public void Shared_columns_agree_on_type_and_nullability()
    {
        var platform = TenantColumns(PlatformModel());
        var core = TenantColumns(CoreModel());

        var mismatches = core
            .Where(c => platform.ContainsKey(c.Key))
            .Where(c => platform[c.Key] != c.Value)
            .Select(c => $"{c.Key}: platform={platform[c.Key]} core={c.Value}")
            .Order()
            .ToArray();

        Assert.True(mismatches.Length == 0, string.Join("; ", mismatches));
    }

    [Fact]
    public void Core_does_not_migrate_the_table_platform_owns()
    {
        // The DESIGN-TIME model, not the runtime one: EF strips the exclusion from the
        // read-optimized model and throws if you ask, which is what a migration is generated
        // from anyway — so this is the model whose answer actually matters.
        var twin = CoreContext().GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Single(e => e.GetTableName() == "tenant" && e.GetSchema() == "platform");

        // Without the exclusion, core's migration emits CREATE TABLE platform.tenant, colliding
        // with platform's own and failing anyway for want of DDL rights on that schema.
        Assert.True(twin.IsTableExcludedFromMigrations());
    }
}
