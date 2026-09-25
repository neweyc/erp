using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Auth;
using AppPlatform.Core.Services;
using AppPlatform.Tenancy;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AppPlatform.Core.Tests;

public class AuthTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IAuthService> _auth = new();
    private readonly AmbientTenantProvider _tenant = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private AcceptInviteFeature.AcceptInviteCommandHandler Accept()
        => new(_auth.Object, _tenant, _clock);

    private SignInFeature.SignInCommandHandler SignIn() => new(_auth.Object, _clock);

    private (UserToken Token, string Plaintext, User User) Invited(
        DateTimeOffset? expiresAt = null, DateTimeOffset? usedAt = null)
    {
        var (plaintext, hash) = TokenGenerator.Create();
        var user = new User { Email = "ada@acme.test", Role = "admin", Status = UserStatus.Invited };
        var token = new UserToken
        {
            TenantId = 1,
            UserId = user.Id,
            Purpose = TokenPurpose.Invite,
            TokenHash = hash,
            CreatedAt = Now,
            ExpiresAt = expiresAt ?? Now.AddDays(7),
            UsedAt = usedAt,
        };

        _auth.Setup(a => a.FindTokenAsync(hash, TokenPurpose.Invite, default)).ReturnsAsync(token);
        _auth.Setup(a => a.FindUserAsync(user.Id, default)).ReturnsAsync(user);

        return (token, plaintext, user);
    }

    [Fact]
    public async Task Accepting_an_invitation_sets_a_password_and_activates_the_account()
    {
        var (token, plaintext, user) = Invited();

        var result = await Accept().Handle(new(plaintext, "correct horse battery"));

        Assert.True(result.Succeeded);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(PasswordHasher.Verify("correct horse battery", user.PasswordHash));
        Assert.Equal(Now, token.UsedAt);
    }

    [Fact]
    public async Task The_tenant_is_taken_from_the_token_not_from_the_caller()
    {
        var (_, plaintext, _) = Invited();

        await Accept().Handle(new(plaintext, "correct horse battery"));

        // Accepting an invitation is anonymous, so there is nothing else to trust. A tenant id
        // supplied by the caller would let anyone activate into any tenant they named.
        Assert.Equal(1, _tenant.TenantId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task An_unrecognised_token_is_refused(string? token)
        => Assert.Equal(AuthProblems.InvalidToken,
            (await Accept().Handle(new(token, "correct horse battery"))).ProblemCode);

    [Fact]
    public async Task An_expired_token_is_refused_with_the_same_message_as_an_unknown_one()
    {
        var (_, plaintext, _) = Invited(expiresAt: Now.AddSeconds(-1));

        var result = await Accept().Handle(new(plaintext, "correct horse battery"));

        // One message for unknown, used and expired: telling them apart says which half of a
        // guessed token was right.
        Assert.Equal(AuthProblems.InvalidToken, result.ProblemCode);
    }

    [Fact]
    public async Task A_used_token_is_refused()
    {
        var (_, plaintext, _) = Invited(usedAt: Now.AddMinutes(-5));

        Assert.Equal(AuthProblems.InvalidToken,
            (await Accept().Handle(new(plaintext, "correct horse battery"))).ProblemCode);
    }

    [Fact]
    public async Task A_short_password_is_refused_before_the_token_is_consumed()
    {
        var (token, plaintext, _) = Invited();

        var result = await Accept().Handle(new(plaintext, "short"));

        Assert.Equal(AuthProblems.WeakPassword, result.ProblemCode);
        // The token must survive: burning it on a rejected password would make the invitation
        // unusable and the person would need a new one for a mistake they can fix.
        Assert.Null(token.UsedAt);
    }

    [Fact]
    public async Task The_plaintext_token_is_never_stored()
    {
        var (token, plaintext, _) = Invited();

        // The database holds a hash; the plaintext exists only in the email. Whoever can read
        // the table must not thereby be able to take over an invited account.
        Assert.NotEqual(plaintext, token.TokenHash);
        Assert.Equal(TokenGenerator.HashOf(plaintext), token.TokenHash);
    }

    [Fact]
    public async Task Signing_in_creates_a_session()
    {
        var user = new User
        {
            Email = "ada@acme.test", Role = "admin", Status = UserStatus.Active,
            PasswordHash = PasswordHasher.Hash("correct horse battery"),
        };
        _auth.Setup(a => a.FindByEmailAsync("ada@acme.test", default)).ReturnsAsync(user);
        Session? added = null;
        _auth.Setup(a => a.AddSession(It.IsAny<Session>())).Callback<Session>(s => added = s);

        var outcome = await SignIn().Handle(1, new("Ada@Acme.test", "correct horse battery"));

        Assert.NotNull(outcome.SessionId);
        Assert.Equal("admin", outcome.Role);
        Assert.Equal(Now.AddDays(7), added!.AbsoluteExpiry);
    }

    [Fact]
    public async Task An_invited_account_cannot_sign_in_before_accepting()
    {
        var user = new User { Email = "ada@acme.test", Role = "admin", Status = UserStatus.Invited };
        _auth.Setup(a => a.FindByEmailAsync("ada@acme.test", default)).ReturnsAsync(user);

        // An invitation that already worked as a login would make the invitation meaningless.
        var outcome = await SignIn().Handle(1, new("ada@acme.test", "anything at all"));

        Assert.Null(outcome.SessionId);
        Assert.Equal(AuthProblems.InvalidCredentials, outcome.ProblemCode);
    }

    [Theory]
    [InlineData("ada@acme.test", "wrong password here")]
    [InlineData("nobody@acme.test", "correct horse battery")]
    public async Task Every_sign_in_failure_reports_the_same_code(string email, string password)
    {
        var user = new User
        {
            Email = "ada@acme.test", Role = "admin", Status = UserStatus.Active,
            PasswordHash = PasswordHasher.Hash("correct horse battery"),
        };
        _auth.Setup(a => a.FindByEmailAsync("ada@acme.test", default)).ReturnsAsync(user);

        var outcome = await SignIn().Handle(1, new(email, password));

        Assert.Equal(AuthProblems.InvalidCredentials, outcome.ProblemCode);
    }

    [Fact]
    public async Task The_session_names_the_callers_tenant_not_the_first_tenant()
    {
        // Regression: the name was read with no tenant id from a table no filter narrows, so every
        // tenant saw whichever tenant the database returned first. Found by the second-tenant e2e.
        var caller = Fake.Caller() with { TenantId = 7 };
        _auth.Setup(a => a.FindUserAsync(caller.UserId!.Value, default))
            .ReturnsAsync(new User { Email = "grace@other.test", Role = "admin" });
        _auth.Setup(a => a.TenantNameAsync(It.IsAny<int>(), default)).ReturnsAsync("E2E Ltd");
        _auth.Setup(a => a.TenantNameAsync(7, default)).ReturnsAsync("Other Ltd");

        var result = await new GetSessionFeature.GetSessionQueryHandler(_auth.Object).Handle(caller);

        var session = Assert.IsType<GetSessionFeature.SessionModel>(result.Value);
        Assert.Equal("Other Ltd", session.TenantName);
    }
}
