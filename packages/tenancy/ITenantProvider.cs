namespace AppPlatform.Tenancy;

/// <summary>
/// The ambient tenant for the current DI scope. Deliberately knows nothing about HTTP:
/// the request-bound implementation lives in the auth package, so a background worker or
/// a test can supply a tenant without dragging in ASP.NET.
/// </summary>
public interface ITenantProvider
{
    /// <summary>
    /// The current tenant, or null where there is none (an anonymous endpoint, or a
    /// worker that has not entered a tenant). Queries over tenant-scoped entities return
    /// nothing in that case rather than returning everything.
    /// </summary>
    int? TenantId { get; }
}

/// <summary>
/// Lets background work — hosted services, CLI commands, provisioning — enter a tenant
/// for the current DI scope, after which filters and stamping behave exactly as they do
/// in a request. Never resolve this from feature code: a handler that can change its own
/// tenant is a handler that can write to someone else's.
/// </summary>
public interface IBackgroundTenantScope
{
    void UseTenant(int tenantId);
}

/// <summary>
/// A plain settable provider for background work and tests. One instance per DI scope —
/// registering it as a singleton would let one worker's tenant leak into another's.
/// </summary>
public sealed class AmbientTenantProvider : ITenantProvider, IBackgroundTenantScope
{
    public int? TenantId { get; private set; }

    public void UseTenant(int tenantId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tenantId, 1);
        TenantId = tenantId;
    }
}
