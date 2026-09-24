namespace AppPlatform.Tenancy;

/// <summary>
/// Marks an entity as belonging to a tenant. <see cref="TenantedDbContext"/> applies a
/// global query filter, stamps TenantId on insert, and rejects cross-tenant writes.
/// Feature code must never assign or filter by TenantId.
/// </summary>
public interface ITenantScoped
{
    int TenantId { get; set; }
}
