using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Tenancy;

/// <summary>
/// Base for every tenant-scoped DbContext. Applies the query filter to each
/// <see cref="ITenantScoped"/> entity and enforces stamping, the cross-tenant guard, and the
/// append-only guard on save, so no derived context has to remember to.
/// </summary>
public abstract class TenantedDbContext(DbContextOptions options, ITenantProvider tenantProvider)
    : DbContext(options)
{
    private readonly ITenantProvider _tenantProvider = tenantProvider;

    /// <summary>
    /// Read through a property rather than captured into the filter expression: EF
    /// re-evaluates a DbContext member on every query, whereas a captured local would be
    /// baked into the compiled query and serve the first request's tenant to everyone.
    /// </summary>
    protected int? CurrentTenantId => _tenantProvider.TenantId;

    /// <summary>Exposed so a boundary test can confirm a context agrees with the provider.</summary>
    internal int? AmbientTenantId => CurrentTenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ApplyTenantFilters(modelBuilder);
    }

    /// <summary>
    /// Call last if a derived OnModelCreating does not chain to base — a filter applied
    /// before an entity is configured is silently dropped.
    /// </summary>
    protected void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var setFilter = typeof(TenantedDbContext)
            .GetMethod(nameof(SetTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned()) continue;
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType)) continue;

            setFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    private void SetTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantScoped
    {
        modelBuilder.Entity<T>()
            .HasQueryFilter(e => CurrentTenantId != null && e.TenantId == CurrentTenantId);
        // Query filters do not scope tracked UPDATE/DELETE statements. Include the
        // original tenant in their WHERE clause, including for detached entities.
        modelBuilder.Entity<T>().Property(e => e.TenantId).IsConcurrencyToken();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AppendOnlyGuard.Enforce(ChangeTracker);
        TenantGuard.Enforce(ChangeTracker, CurrentTenantId);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AppendOnlyGuard.Enforce(ChangeTracker);
        TenantGuard.Enforce(ChangeTracker, CurrentTenantId);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
