using System.Diagnostics;
using AppPlatform.Auth;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Auth;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AppPlatform.Platform.Tests;

public class SignInTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IOperatorSessionStore> _sessions = new();
    private readonly List<PlatformAuditLog> _audit = [];
    private readonly Guid _sessionId = Guid.NewGuid();

    public SignInTests()
    {
        var audit = new Mock<ITenantAuditWriter>();
        audit.Setup(a => a.Write(It.IsAny<PlatformAuditLog>())).Callback<PlatformAuditLog>(_audit.Add);
        Audit = audit.Object;

        _sessions.Setup(s => s.CreateSessionWithCodeAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), default))
            .ReturnsAsync(_sessionId);
    }

    private ITenantAuditWriter Audit { get; }

    private readonly Encryption.KeyRing _keyRing = TestKeyRing.Create();

    /// <summary>The enrolled operator's authenticator secret, as their phone holds it.</summary>
    private string _secret = "";

    private string CodeNow() => Totp.CodeAt(_secret, Totp.StepAt(Now));

    private SignInFeature.SignInCommandHandler Handler()
        => new(_sessions.Object, Audit, _keyRing, new FakeTimeProvider(Now));

    private PlatformUser Operator(string password = "hunter2", bool active = true, bool enrolled = true)
    {
        var user = new PlatformUser
        {
            Email = "op@example.com",
            // DefaultIterations, deliberately, not a cheap cost: the timing test below is only
            // meaningful when the stored hash costs the same as the dummy one.
            PasswordHash = PasswordHasher.Hash(password),
            Active = active,
        };
        if (enrolled) _secret = OperatorMfa.Enroll(user, _keyRing);
        _sessions.Setup(s => s.FindByEmailAsync("op@example.com", default)).ReturnsAsync(user);
        return user;
    }

    [Fact]
    public async Task Correct_credentials_create_a_session_and_audit_the_sign_in()
    {
        var user = Operator();

        var outcome = await Handler().Handle(new("OP@Example.com", "hunter2", CodeNow()));

        Assert.Equal(_sessionId, outcome.SessionId);
        Assert.Null(outcome.ProblemCode);
        Assert.Equal("operator.signed_in", Assert.Single(_audit).Action);
        _sessions.Verify(s => s.CreateSessionWithCodeAsync(user.Id, user.TotpSecretVersion, Totp.StepAt(Now), Now, default), Times.Once);
    }

    [Theory]
    [InlineData("op@example.com", "wrong")]
    [InlineData("nobody@example.com", "hunter2")]
    public async Task Every_failure_returns_the_same_problem_code(string email, string password)
    {
        Operator();

        var outcome = await Handler().Handle(new(email, password));

        // Distinguishing "no such operator" from "wrong password" tells an attacker which half
        // of the guess was right, which turns credential stuffing into account enumeration.
        Assert.Equal(OperatorProblems.InvalidCredentials, outcome.ProblemCode);
        Assert.Null(outcome.SessionId);
    }

    [Fact]
    public async Task A_deactivated_operator_cannot_sign_in_even_with_the_right_password()
    {
        Operator(active: false);

        var outcome = await Handler().Handle(new("op@example.com", "hunter2"));

        Assert.Equal(OperatorProblems.InvalidCredentials, outcome.ProblemCode);
        AssertStoreNotAsked();
    }

    [Fact]
    public async Task Failures_are_audited_too()
    {
        Operator();

        await Handler().Handle(new("op@example.com", "wrong"));

        // Repeated failures against an operator account are the signal that matters most on
        // this surface, and they are invisible if only successes are recorded.
        var entry = Assert.Single(_audit);
        Assert.Equal("operator.sign_in_failed", entry.Action);
        Assert.Null(entry.PlatformUserId);
    }

    [Fact]
    public async Task An_unknown_address_costs_about_as_much_time_as_a_wrong_password()
    {
        Operator();

        var unknown = await Time(() => Handler().Handle(new("nobody@example.com", "hunter2")));
        var wrong = await Time(() => Handler().Handle(new("op@example.com", "wrong")));

        // Returning early on an unknown address makes sign-in measurably faster for addresses
        // that do not exist, which enumerates the operator accounts. The dummy hash is what
        // keeps the two paths comparable.
        // Caught a real inversion when first written: the dummy hash cost DefaultIterations
        // while the test's stored hash cost 1,000, making the UNKNOWN address six times slower
        // — just as enumerable as being faster, and in production it is stored hashes drifting
        // below the default that would cause it.
        var slower = Math.Max(unknown, wrong);
        var faster = Math.Max(1, Math.Min(unknown, wrong));
        Assert.True(slower < faster * 5,
            $"unknown={unknown}ms wrong={wrong}ms — timing differs enough to enumerate");
    }

    [Theory]
    [InlineData(null, "pw")]
    [InlineData("op@example.com", null)]
    [InlineData("", "pw")]
    public async Task Missing_credentials_fail_like_any_other_bad_attempt(string? email, string? password)
    {
        Operator();

        var outcome = await Handler().Handle(new(email, password));

        Assert.Equal(OperatorProblems.InvalidCredentials, outcome.ProblemCode);
    }

    [Fact]
    public async Task The_right_password_without_a_code_asks_for_one_and_creates_no_session()
    {
        Operator();

        var outcome = await Handler().Handle(new("op@example.com", "hunter2"));

        Assert.Equal(OperatorProblems.MfaRequired, outcome.ProblemCode);
        AssertStoreNotAsked();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_wrong_code_is_refused_and_creates_no_session(bool rightLength)
    {
        Operator();
        // The current code with its first digit changed is always wrong; a five-digit one is malformed.
        var current = CodeNow();
        var wrong = rightLength ? $"{(current[0] - '0' + 1) % 10}{current[1..]}" : current[..5];

        var outcome = await Handler().Handle(new("op@example.com", "hunter2", wrong));

        Assert.Equal(OperatorProblems.MfaCodeInvalid, outcome.ProblemCode);
        AssertStoreNotAsked();
        AssertStoreNotAsked();
    }

    [Fact]
    public async Task A_wrong_password_with_a_right_code_says_nothing_about_the_code()
    {
        // Everything before the password check answers invalid_credentials, so a guesser learns
        // nothing about whether the account exists or has an authenticator.
        Operator();

        var outcome = await Handler().Handle(new("op@example.com", "wrong", CodeNow()));

        Assert.Equal(OperatorProblems.InvalidCredentials, outcome.ProblemCode);
        AssertStoreNotAsked();
    }

    [Fact]
    public async Task A_code_the_store_will_not_use_is_refused()
    {
        // The store answers null when another sign-in has used this step or a later one, or the
        // authenticator was reset after the code was checked: the races its transaction settles.
        var user = Operator();
        _sessions.Setup(s => s.CreateSessionWithCodeAsync(user.Id, It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), default))
            .ReturnsAsync((Guid?)null);

        var outcome = await Handler().Handle(new("op@example.com", "hunter2", CodeNow()));

        Assert.Equal(OperatorProblems.MfaCodeInvalid, outcome.ProblemCode);
        Assert.Null(outcome.SessionId);
    }

    [Fact]
    public async Task A_code_at_or_before_the_last_used_step_is_refused_without_asking_the_store()
    {
        var user = Operator();
        user.TotpLastUsedStep = Totp.StepAt(Now);

        var outcome = await Handler().Handle(new("op@example.com", "hunter2", CodeNow()));

        Assert.Equal(OperatorProblems.MfaCodeInvalid, outcome.ProblemCode);
        AssertStoreNotAsked();
    }

    [Fact]
    public async Task An_operator_with_no_authenticator_cannot_sign_in()
    {
        // MFA is mandatory for operators. No enrolment means no sign-in, not a password-only one.
        Operator(enrolled: false);

        var outcome = await Handler().Handle(new("op@example.com", "hunter2", "123456"));

        Assert.Equal(OperatorProblems.MfaNotEnrolled, outcome.ProblemCode);
        AssertStoreNotAsked();
    }

    [Fact]
    public async Task The_store_is_told_which_secret_version_the_code_was_checked_against()
    {
        // So that a reset committed after the check makes the store refuse, rather than admit a
        // code from the secret that was just replaced.
        var user = Operator();
        user.TotpSecretVersion = 7;

        await Handler().Handle(new("op@example.com", "hunter2", CodeNow()));

        _sessions.Verify(s => s.CreateSessionWithCodeAsync(user.Id, 7, Totp.StepAt(Now), Now, default), Times.Once);
    }

    /// <summary>Refused before the store was asked to use a code or create a session.</summary>
    private void AssertStoreNotAsked()
        => _sessions.Verify(s => s.CreateSessionWithCodeAsync(
            It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateTimeOffset>(), default), Times.Never);

    private static async Task<long> Time(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        return stopwatch.ElapsedMilliseconds;
    }
}
