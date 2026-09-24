using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AppPlatform.Tenancy;

/// <summary>
/// Insert stamping and the cross-tenant write guard, as a static over a ChangeTracker so
/// a DbContext that cannot inherit <see cref="TenantedDbContext"/> can still call it.
/// </summary>
public static class TenantGuard
{
    /// <summary>
    /// Stamps TenantId on every added tenant-scoped entity and rejects any modify or
    /// delete whose entity belongs to a different tenant. Call immediately before
    /// SaveChanges, after anything that stages further rows.
    /// </summary>
    public static void Enforce(ChangeTracker changeTracker, int? currentTenantId)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);

        foreach (var entry in changeTracker.Entries())
        {
            if (entry.Entity is not ITenantScoped scoped) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    // No ambient tenant is a hard failure. EMS honoured a pre-set
                    // TenantId here as a narrow path for reviewed anonymous flows, which
                    // made "assign TenantId yourself" a working bypass of the whole
                    // mechanism. Provisioning and other pre-session writes enter a scope
                    // through IBackgroundTenantScope instead, so the rule has no
                    // exception to erode.
                    if (currentTenantId is not { } tenantId)
                    {
                        throw new TenantScopeViolationException(
                            $"Cannot insert {entry.Metadata.ClrType.Name} without a tenant context. " +
                            "Background and provisioning writes must enter one via IBackgroundTenantScope.");
                    }

                    if (scoped.TenantId != 0 && scoped.TenantId != tenantId)
                    {
                        throw new TenantScopeViolationException(
                            $"Cannot insert {entry.Metadata.ClrType.Name} for tenant {scoped.TenantId} " +
                            $"while the current tenant is {tenantId}.");
                    }

                    scoped.TenantId = tenantId;
                    break;

                case EntityState.Modified or EntityState.Deleted:
                    var tenant = entry.Property(nameof(ITenantScoped.TenantId));
                    if (currentTenantId is null || scoped.TenantId != currentTenantId
                        || (int)tenant.OriginalValue! != currentTenantId)
                        throw new TenantScopeViolationException(
                            $"Cross-tenant write rejected for {entry.Metadata.ClrType.Name}. " +
                            "The current and original tenant must match the scope.");
                    if (!tenant.Metadata.IsConcurrencyToken && !tenant.Metadata.IsPrimaryKey())
                        throw new TenantScopeViolationException(
                            "TenantId must be a concurrency token or part of the primary key.");
                    break;
            }
        }
    }
}
