namespace AppPlatform.Auth;

/// <summary>The outcome of authenticating one request. Exactly one of the two properties is set.</summary>
public sealed record AuthenticationResult
{
    private AuthenticationResult() { }

    public Caller? Caller { get; private init; }
    public string? ProblemCode { get; private init; }

    public bool Succeeded => Caller is not null;

    public static AuthenticationResult Success(Caller caller) => new() { Caller = caller };
    public static AuthenticationResult Fail(string problemCode) => new() { ProblemCode = problemCode };
}

/// <summary>
/// Turns a session row into a caller, or into a reason there is none. Pure: no clock, no
/// database, no HTTP — the rules here are the substance of access control and they should
/// be provable without standing anything up.
/// </summary>
public static class SessionEvaluator
{
    /// <param name="context">The row, or null when the session id matched nothing.</param>
    /// <param name="ticketAccess">The access stamp recorded in the cookie when it was issued.</param>
    public static AuthenticationResult Evaluate(
        SessionContext? context, string? ticketAccess, DateTimeOffset now)
    {
        if (context is null) return AuthenticationResult.Fail(AuthProblem.SessionInvalid);

        // Order is deliberate: the most specific reason wins, so the message a user sees
        // names what actually happened. Checking expiry first would report "expired" to
        // someone whose access was revoked, and they would simply sign in again.
        if (context.RevokedAt is not null)
            return AuthenticationResult.Fail(AuthProblem.SessionRevoked);

        if (!context.UserActive)
            return AuthenticationResult.Fail(AuthProblem.UserDeactivated);

        // Roles are compared against the cookie's claim, not merely read. Permissions below are
        // computed from the live row either way, so a demotion is already effective; this ends
        // the session so the shell reloads and stops offering what the user can no longer do.
        if (ticketAccess is not null
            && !string.Equals(ticketAccess, AppRoles.AccessStamp(context.Role, context.AppRoles), StringComparison.Ordinal))
            return AuthenticationResult.Fail(AuthProblem.RoleChanged);

        if (!context.MfaSatisfied)
            return AuthenticationResult.Fail(AuthProblem.MfaRequired);

        if (now >= context.AbsoluteExpiry)
            return AuthenticationResult.Fail(AuthProblem.SessionInvalid);

        if (IsIdle(context, now))
            return AuthenticationResult.Fail(AuthProblem.SessionIdleTimeout);

        // Retired is terminal and its sessions are revoked with it, so it fails
        // authentication. Suspended is reversible and the customer still owns their data,
        // so it does NOT: the principal survives and authorization narrows what it can
        // reach. Collapsing the two would break the promised export.
        if (context.TenantStatus == TenantStatus.Retired)
            return AuthenticationResult.Fail(AuthProblem.TenantRetired);

        return AuthenticationResult.Success(new Caller
        {
            PrincipalId = context.UserId,
            SessionId = context.SessionId,
            Kind = PrincipalKind.User,
            UserId = context.UserId,
            TenantId = context.TenantId,
            CompanyId = context.CompanyId,
            Role = context.Role,
            AppRoles = context.AppRoles,
            Permissions = RoleCatalogue.PermissionsFor(context.Role, context.AppRoles, context.LicensedApps),
            EmployeeId = context.EmployeeId,
            LicensedApps = context.LicensedApps,
            TenantSuspended = context.TenantStatus == TenantStatus.Suspended,
        });
    }

    /// <summary>
    /// Idle is measured against the WALL CLOCK, so a laptop that slept counts as idle for
    /// the time it slept.
    /// </summary>
    private static bool IsIdle(SessionContext context, DateTimeOffset now)
        => context.IdleTimeoutMinutes is { } minutes
           && now - context.LastSeenAt >= TimeSpan.FromMinutes(minutes);
}
