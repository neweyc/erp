using AppPlatform.Tenancy;

namespace AppPlatform.Auth;

/// <summary>The authenticated caller for the current DI scope.</summary>
public interface ICallerContext
{
    Caller? Caller { get; }

    /// <summary>For code that cannot proceed anonymously. Endpoints are authorized before
    /// a handler runs, so reaching this with no caller is a wiring bug, not a 401.</summary>
    Caller Require();
}

/// <summary>
/// Holds the caller, and supplies the tenant to <see cref="ITenantProvider"/>.
///
/// This is the join between the two packages, and the reason tenancy defines its provider
/// as an interface rather than reading claims itself: query filters and insert stamping
/// take the tenant from the AUTHENTICATED SESSION and from nowhere else. A tenant id in a
/// header, a query string, or a request body is an attack, not a feature — and with this
/// wiring there is no code path that could honour one.
/// </summary>
public sealed class CallerContext : ICallerContext, ITenantProvider, IBackgroundTenantScope
{
    private Caller? _caller;
    private int? _backgroundTenantId;

    public Caller? Caller => _caller;

    public int? TenantId => _caller?.TenantId ?? _backgroundTenantId;

    public Caller Require() => _caller
        ?? throw new InvalidOperationException(
            "No authenticated caller in this scope. An endpoint that needs one must require " +
            "authorization; reaching a handler without a caller is a wiring fault.");

    public void SetCaller(Caller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Set once. A second call would mean something re-authenticated mid-request, and
        // silently swapping the tenant underneath work already done is how a partially
        // written transaction ends up split across two tenants.
        if (_caller is not null)
            throw new InvalidOperationException("The caller for this scope has already been set.");

        _caller = caller;
    }

    /// <summary>
    /// Background work only — hosted services, CLI commands, provisioning. Refused once a
    /// caller exists: a request that could re-scope itself to another tenant is precisely
    /// the hole the rest of this design closes.
    /// </summary>
    public void UseTenant(int tenantId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tenantId, 1);

        if (_caller is not null)
            throw new InvalidOperationException(
                "Cannot enter a background tenant scope inside an authenticated request.");

        _backgroundTenantId = tenantId;
    }
}
