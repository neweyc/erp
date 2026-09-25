using AppPlatform.Audit;
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
///
/// It supplies the AUDIT ACTOR for the same reason: the principal a change is attributed to comes
/// from the authenticated session, so there is no request path that can name someone else.
/// </summary>
public sealed class CallerContext
    : ICallerContext, ITenantProvider, IBackgroundTenantScope, ICookieAuthenticationState,
      IAuditActor, IAuditActorScope
{
    /// <summary>An API key is not sent by a browser, so only a user session carries CSRF risk.</summary>
    public bool IsCookieAuthenticated => _caller?.Kind is PrincipalKind.User;

    private Caller? _caller;
    private int? _backgroundTenantId;
    private AuditActor? _backgroundActor;

    public Caller? Caller => _caller;

    public int? TenantId => _caller?.TenantId ?? _backgroundTenantId;

    public AuditActor? Current => _caller switch
    {
        { Kind: PrincipalKind.User } user => AuditActor.User(user.PrincipalId),
        { Kind: PrincipalKind.ApiKey } key => AuditActor.ApiKey(key.PrincipalId),
        _ => _backgroundActor,
    };

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

    /// <summary>
    /// Pre-session work only — accepting an invitation, provisioning. Refused once a caller exists,
    /// for the same reason as <see cref="UseTenant"/>: a request that could re-attribute its own
    /// writes would make the audit log say whatever the request wanted.
    /// </summary>
    public void UseActor(AuditActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (_caller is not null)
            throw new InvalidOperationException(
                "Cannot declare an audit actor inside an authenticated request; the caller is the actor.");

        _backgroundActor = actor;
    }
}
