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

        _sessions.Setup(s => s.CreateSessionAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), default))
            .ReturnsAsync(_sessionId);
    }

    private ITenantAuditWriter Audit { get; }

    private SignInFeature.SignInCommandHandler Handler()
        => new(_sessions.Object, Audit, new FakeTimeProvider(Now));

    private PlatformUser Operator(string password = "hunter2", bool active = true)
    {
        var user = new PlatformUser
        {
            Email = "op@example.com",
            // DefaultIterations, deliberately, not a cheap cost: the timing test below is only
            // meaningful when the stored hash costs the same as the dummy one.
            PasswordHash = PasswordHasher.Hash(password),
            Active = active,
        };
        _sessions.Setup(s => s.FindByEmailAsync("op@example.com", default)).ReturnsAsync(user);
        return user;
    }

    [Fact]
    public async Task Correct_credentials_create_a_session_and_audit_the_sign_in()
    {
        var user = Operator();

        var outcome = await Handler().Handle(new("OP@Example.com", "hunter2"));

        Assert.Equal(_sessionId, outcome.SessionId);
        Assert.Null(outcome.ProblemCode);
        Assert.Equal("operator.signed_in", Assert.Single(_audit).Action);
        _sessions.Verify(s => s.CreateSessionAsync(user.Id, Now, default), Times.Once);
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
        _sessions.Verify(s => s.CreateSessionAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), default), Times.Never);
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

    private static async Task<long> Time(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        return stopwatch.ElapsedMilliseconds;
    }
}
