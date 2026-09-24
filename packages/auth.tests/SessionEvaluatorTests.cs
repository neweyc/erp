namespace AppPlatform.Auth.Tests;

public class SessionEvaluatorTests
{
    [Fact]
    public void A_valid_session_produces_a_user_caller()
    {
        var result = SessionEvaluator.Evaluate(Build.Session(), "admin", Build.Now);

        Assert.True(result.Succeeded);
        var caller = result.Caller!;
        Assert.Equal(PrincipalKind.User, caller.Kind);
        Assert.Equal(caller.UserId, caller.PrincipalId);
        Assert.Equal(1, caller.TenantId);
        Assert.Equal(["tickets"], caller.LicensedApps);
        Assert.False(caller.TenantSuspended);
    }

    [Fact]
    public void An_unknown_session_id_is_invalid()
        => Assert.Equal(AuthProblem.SessionInvalid,
            SessionEvaluator.Evaluate(null, "admin", Build.Now).ProblemCode);

    [Fact]
    public void A_revoked_session_is_rejected()
        => Assert.Equal(AuthProblem.SessionRevoked,
            SessionEvaluator.Evaluate(
                Build.Session(revokedAt: Build.Now.AddMinutes(-1)), "admin", Build.Now).ProblemCode);

    [Fact]
    public void A_deactivated_user_is_rejected()
        => Assert.Equal(AuthProblem.UserDeactivated,
            SessionEvaluator.Evaluate(Build.Session(userActive: false), "admin", Build.Now).ProblemCode);

    [Fact]
    public void A_role_change_invalidates_the_ticket_immediately()
    {
        // The whole point of revalidating per request: a cookie issued to an admin must
        // stop working the moment they are demoted, not when it expires.
        var result = SessionEvaluator.Evaluate(Build.Session(role: "member"), "admin", Build.Now);

        Assert.Equal(AuthProblem.RoleChanged, result.ProblemCode);
    }

    [Fact]
    public void An_expired_session_is_invalid()
        => Assert.Equal(AuthProblem.SessionInvalid,
            SessionEvaluator.Evaluate(
                Build.Session(absoluteExpiry: Build.Now.AddSeconds(-1)), "admin", Build.Now).ProblemCode);

    [Fact]
    public void Expiry_is_inclusive_at_the_boundary()
    {
        // A session whose absolute expiry is exactly now is over. Using > rather than >=
        // leaves a one-tick window that is untestable in production and trivially wrong here.
        var result = SessionEvaluator.Evaluate(
            Build.Session(absoluteExpiry: Build.Now), "admin", Build.Now);

        Assert.Equal(AuthProblem.SessionInvalid, result.ProblemCode);
    }

    [Fact]
    public void Outstanding_mfa_is_rejected()
        => Assert.Equal(AuthProblem.MfaRequired,
            SessionEvaluator.Evaluate(Build.Session(mfaSatisfied: false), "admin", Build.Now).ProblemCode);

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(31, false)]
    public void Idle_timeout_fires_once_the_window_has_fully_elapsed(int minutesIdle, bool shouldSucceed)
    {
        var result = SessionEvaluator.Evaluate(
            Build.Session(idleTimeoutMinutes: 30, lastSeenAt: Build.Now.AddMinutes(-minutesIdle)),
            "admin", Build.Now);

        Assert.Equal(shouldSucceed, result.Succeeded);
        if (!shouldSucceed) Assert.Equal(AuthProblem.SessionIdleTimeout, result.ProblemCode);
    }

    [Fact]
    public void No_idle_window_means_no_idle_timeout()
    {
        // Null is "off", and must not be read as zero — which would sign everyone out on
        // their next request.
        var result = SessionEvaluator.Evaluate(
            Build.Session(idleTimeoutMinutes: null, lastSeenAt: Build.Now.AddDays(-3)),
            "admin", Build.Now);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void A_retired_tenant_fails_authentication()
        => Assert.Equal(AuthProblem.TenantRetired,
            SessionEvaluator.Evaluate(
                Build.Session(status: TenantStatus.Retired), "admin", Build.Now).ProblemCode);

    [Fact]
    public void A_suspended_tenant_still_authenticates_and_is_flagged()
    {
        // The finding this encodes: rejecting suspension here would discard the principal,
        // and the data export a suspended customer is promised would have no authenticated
        // caller left to authorize.
        var result = SessionEvaluator.Evaluate(
            Build.Session(status: TenantStatus.Suspended), "admin", Build.Now);

        Assert.True(result.Succeeded);
        Assert.True(result.Caller!.TenantSuspended);
    }

    [Fact]
    public void Revocation_is_reported_ahead_of_expiry()
    {
        // Both are true; the user must be told the specific one. Reporting "expired" to
        // someone whose access was revoked invites them to simply sign in again.
        var result = SessionEvaluator.Evaluate(
            Build.Session(revokedAt: Build.Now.AddDays(-1), absoluteExpiry: Build.Now.AddDays(-1)),
            "admin", Build.Now);

        Assert.Equal(AuthProblem.SessionRevoked, result.ProblemCode);
    }

    [Fact]
    public void A_null_ticket_role_skips_the_comparison()
    {
        // First request after sign-in, before a role claim exists on the ticket. Treating
        // null as a mismatch would reject every freshly issued session.
        Assert.True(SessionEvaluator.Evaluate(Build.Session(), null, Build.Now).Succeeded);
    }
}
