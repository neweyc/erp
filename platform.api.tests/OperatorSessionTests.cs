using AppPlatform.Platform.Auth;

namespace AppPlatform.Platform.Tests;

public class OperatorSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static OperatorSessionContext Session(
        bool userActive = true,
        bool mfaSatisfied = true,
        DateTimeOffset? revokedAt = null,
        DateTimeOffset? lastSeenAt = null,
        DateTimeOffset? absoluteExpiry = null)
        => new()
        {
            SessionId = Guid.NewGuid(),
            PlatformUserId = Guid.NewGuid(),
            Email = "op@example.com",
            UserActive = userActive,
            MfaSatisfied = mfaSatisfied,
            LastSeenAt = lastSeenAt ?? Now,
            AbsoluteExpiry = absoluteExpiry ?? Now.AddHours(8),
            RevokedAt = revokedAt,
        };

    [Fact]
    public void A_valid_session_yields_a_principal()
    {
        var (principal, problem) = OperatorSessionEvaluator.Evaluate(Session(), Now);

        Assert.NotNull(principal);
        Assert.Null(problem);
        Assert.Equal("op@example.com", principal.Email);
    }

    [Fact]
    public void An_unknown_session_is_invalid()
        => Assert.Equal(OperatorProblems.SessionInvalid,
            OperatorSessionEvaluator.Evaluate(null, Now).ProblemCode);

    [Fact]
    public void A_revoked_session_is_rejected()
        => Assert.Equal(OperatorProblems.SessionRevoked,
            OperatorSessionEvaluator.Evaluate(Session(revokedAt: Now.AddMinutes(-1)), Now).ProblemCode);

    [Fact]
    public void A_deactivated_operator_is_rejected()
        => Assert.Equal(OperatorProblems.OperatorDeactivated,
            OperatorSessionEvaluator.Evaluate(Session(userActive: false), Now).ProblemCode);

    [Fact]
    public void Unsatisfied_mfa_is_rejected_per_request_not_only_at_sign_in()
    {
        // The enforcement path exists now so that M2 changes only the enrolment requirement.
        // A design where enforcement arrives with the requirement tends to arrive with neither.
        Assert.Equal(OperatorProblems.MfaRequired,
            OperatorSessionEvaluator.Evaluate(Session(mfaSatisfied: false), Now).ProblemCode);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    public void Idle_beyond_thirty_minutes_ends_the_session(int minutesIdle, bool shouldSucceed)
    {
        var (principal, problem) = OperatorSessionEvaluator.Evaluate(
            Session(lastSeenAt: Now.AddMinutes(-minutesIdle)), Now);

        Assert.Equal(shouldSucceed, principal is not null);
        if (!shouldSucceed) Assert.Equal(OperatorProblems.SessionIdleTimeout, problem);
    }

    [Fact]
    public void An_expired_session_is_invalid()
        => Assert.Equal(OperatorProblems.SessionInvalid,
            OperatorSessionEvaluator.Evaluate(Session(absoluteExpiry: Now), Now).ProblemCode);

    [Fact]
    public void An_operator_session_is_shorter_lived_than_a_tenant_users()
    {
        // An operator session reaches EVERY tenant; a tenant user's reaches one. The asymmetry
        // is deliberate, and pinning it here stops it being "simplified" to match later.
        Assert.Equal(TimeSpan.FromHours(8), OperatorSessionEvaluator.AbsoluteLifetime);
        Assert.True(OperatorSessionEvaluator.AbsoluteLifetime < TimeSpan.FromDays(7));
        Assert.Equal(TimeSpan.FromMinutes(30), OperatorSessionEvaluator.IdleWindow);
    }

    [Fact]
    public void Revocation_is_reported_ahead_of_expiry()
    {
        var (_, problem) = OperatorSessionEvaluator.Evaluate(
            Session(revokedAt: Now.AddDays(-1), absoluteExpiry: Now.AddDays(-1)), Now);

        Assert.Equal(OperatorProblems.SessionRevoked, problem);
    }
}
