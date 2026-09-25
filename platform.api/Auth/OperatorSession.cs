namespace AppPlatform.Platform.Auth;

/// <summary>Who is operating the console. Deliberately not a tenant <c>Caller</c>.</summary>
public sealed record OperatorPrincipal
{
    public required Guid PlatformUserId { get; init; }
    public required Guid SessionId { get; init; }
    public required string Email { get; init; }
}

/// <summary>One row of operator session state, read back on every request.</summary>
public sealed record OperatorSessionContext
{
    public required Guid SessionId { get; init; }
    public required Guid PlatformUserId { get; init; }
    public required string Email { get; init; }
    public required bool UserActive { get; init; }
    public required bool MfaSatisfied { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public required DateTimeOffset AbsoluteExpiry { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public static class OperatorProblems
{
    public const string SessionInvalid = "session_invalid";
    public const string SessionRevoked = "session_revoked";
    public const string SessionIdleTimeout = "session_idle_timeout";
    public const string OperatorDeactivated = "operator_deactivated";
    public const string MfaRequired = "mfa_required";
    public const string InvalidCredentials = "invalid_credentials";
}

/// <summary>
/// Operator session rules. A separate evaluator from the tenant one, because an operator has no
/// tenant, no company, and no entitlements — forcing them through <c>Caller</c> would mean
/// inventing values for all three, and an invented tenant id is exactly the kind of thing that
/// later gets used.
/// </summary>
public static class OperatorSessionEvaluator
{
    /// <summary>
    /// Shorter than a tenant user's 7 days, because an operator session reaches EVERY tenant.
    /// The asymmetry is the point.
    /// </summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);

    /// <summary>Not configurable. An operator does not get to opt out of it.</summary>
    public static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(30);

    public static (OperatorPrincipal? Principal, string? ProblemCode) Evaluate(
        OperatorSessionContext? context, DateTimeOffset now)
    {
        if (context is null) return (null, OperatorProblems.SessionInvalid);
        if (context.RevokedAt is not null) return (null, OperatorProblems.SessionRevoked);
        if (!context.UserActive) return (null, OperatorProblems.OperatorDeactivated);

        // MFA is checked HERE, not only at sign-in, so that the day enrolment becomes mandatory
        // (M2) the enforcement path already exists and only the enrolment requirement changes.
        // A design where enforcement arrives with the requirement tends to arrive with neither.
        if (!context.MfaSatisfied) return (null, OperatorProblems.MfaRequired);

        if (now >= context.AbsoluteExpiry) return (null, OperatorProblems.SessionInvalid);

        // Measured against the wall clock, so a laptop that slept counts as idle for the time
        // it slept.
        if (now - context.LastSeenAt >= IdleWindow) return (null, OperatorProblems.SessionIdleTimeout);

        return (new OperatorPrincipal
        {
            PlatformUserId = context.PlatformUserId,
            SessionId = context.SessionId,
            Email = context.Email,
        }, null);
    }
}
