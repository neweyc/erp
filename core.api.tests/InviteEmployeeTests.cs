using AppPlatform.Api;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.Extensions.Time.Testing;
using Moq;
using System.Text.Json;
using static AppPlatform.Core.Features.Employees.InviteEmployeeFeature;

namespace AppPlatform.Core.Tests;

public class InviteEmployeeTests
{
    private readonly Mock<IEmployeeService> _employees = new();
    private readonly Mock<IUserService> _users = new();
    private readonly RecordingOutbox _outbox = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private InviteEmployeeCommandHandler Handler()
        => new(_employees.Object, _users.Object, _outbox, new FakeTimeProvider(Now));

    private void Existing(Employee employee)
        => _employees.Setup(s => s.FindByPublicIdAsync(employee.PublicId, default)).ReturnsAsync(employee);

    [Fact]
    public async Task Inviting_an_employee_creates_an_invited_account_linked_by_id()
    {
        var employee = Fake.Employee();
        Existing(employee);
        User? added = null;
        _users.Setup(s => s.Add(It.IsAny<User>())).Callback<User>(u => added = u);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new("manager"));

        Assert.True(result.Succeeded);
        Assert.NotNull(added);
        Assert.Equal(employee.Id, added.EmployeeId);
        Assert.Equal(UserStatus.Invited, added.Status);
        Assert.Equal("manager", added.Role);
    }

    [Fact]
    public async Task The_invitation_email_is_staged_and_commits_with_the_account()
    {
        var employee = Fake.Employee();
        Existing(employee);

        await Handler().Handle(Fake.Caller(), employee.PublicId, new("member"));

        var message = Assert.Single(_outbox.Messages);
        Assert.Equal(OutboxTransports.Email, message.Transport);
        Assert.Equal(employee.Email, message.Destination);

        // Staged, not sent. Sending first leaves a recipient holding a link to an invitation
        // that does not exist if the commit then fails.
        _employees.Verify(s => s.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task The_invitation_carries_a_token_the_invitee_can_accept_with()
    {
        // D17: this invitation used to carry no token, and accepting requires one, so an invited
        // employee could never activate their account — and could not be invited again, because
        // the account now existed.
        var employee = Fake.Employee();
        Existing(employee);
        User? user = null;
        UserToken? token = null;
        _users.Setup(s => s.Add(It.IsAny<User>())).Callback<User>(u => user = u);
        _users.Setup(s => s.AddToken(It.IsAny<UserToken>())).Callback<UserToken>(t => token = t);

        await Handler().Handle(Fake.Caller(), employee.PublicId, new("member"));

        Assert.NotNull(user);
        Assert.NotNull(token);
        Assert.Equal(user.Id, token.UserId);
        Assert.Equal(TokenPurpose.Invite, token.Purpose);
        Assert.Equal(Now.AddDays(7), token.ExpiresAt);
        Assert.Null(token.UsedAt);

        // The token row stores only the hash; the plaintext goes in the email (and, until the
        // outbox prunes it, the staged message). AcceptInvite hashes the link to look it up.
        var payload = JsonDocument.Parse(Assert.Single(_outbox.Messages).Payload).RootElement;
        var plaintext = payload.GetProperty("token").GetString()!;
        Assert.Equal(TokenGenerator.HashOf(plaintext), token.TokenHash);
        Assert.NotEqual(plaintext, token.TokenHash);
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("member")]
    public async Task Only_an_administrator_can_invite(string role)
    {
        // An invitation can grant admin, so letting anyone else send one is an escalation: a
        // member creates an employee at an address they control, invites it as admin, accepts.
        var employee = Fake.Employee();
        Existing(employee);

        var result = await Handler().Handle(Fake.Caller(role), employee.PublicId, new("admin"));

        Assert.Equal(CommandOutcome.Forbidden, result.Outcome);
        Assert.Equal(CoreProblems.NotPermitted, result.ProblemCode);
        _employees.Verify(s => s.FindByPublicIdAsync(It.IsAny<string>(), default), Times.Never);
        _users.Verify(s => s.Add(It.IsAny<User>()), Times.Never);
        _users.Verify(s => s.AddToken(It.IsAny<UserToken>()), Times.Never);
        Assert.Empty(_outbox.Messages);
        _employees.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task A_refused_invitation_issues_no_token()
    {
        var employee = Fake.Employee(status: EmployeeStatus.Terminated);
        Existing(employee);

        await Handler().Handle(Fake.Caller(), employee.PublicId, new("member"));

        _users.Verify(s => s.AddToken(It.IsAny<UserToken>()), Times.Never);
    }

    [Fact]
    public async Task An_id_of_the_wrong_kind_is_refused_before_any_lookup()
    {
        // A ticket id here would otherwise find no employee and return 404, hiding the real
        // mistake: two kinds of id sharing one route parameter.
        var result = await Handler().Handle(Fake.Caller(), PublicId.New("tkt").ToString(), new("member"));

        Assert.Equal(CommandOutcome.NotFound, result.Outcome);
        _employees.Verify(s => s.FindByPublicIdAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("superuser")]
    public async Task An_invalid_role_is_rejected_with_the_valid_values_named(string? role)
    {
        var result = await Handler().Handle(Fake.Caller(), Fake.Employee().PublicId, new(role));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.Contains("admin", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_terminated_employee_cannot_be_invited()
    {
        var employee = Fake.Employee(status: EmployeeStatus.Terminated);
        Existing(employee);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new("member"));

        Assert.Equal(CoreProblems.EmployeeTerminated, result.ProblemCode);
        Assert.Empty(_outbox.Messages);
    }

    [Fact]
    public async Task An_employee_with_no_email_cannot_be_invited()
    {
        var employee = Fake.Employee(email: null!);
        Existing(employee);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new("member"));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.Contains("nowhere to send", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_employee_who_already_has_an_account_is_a_conflict()
    {
        var employee = Fake.Employee();
        Existing(employee);
        _users.Setup(s => s.FindByEmployeeAsync(employee.Id, default))
            .ReturnsAsync(new User { Email = employee.Email!, Role = "member" });

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new("member"));

        Assert.Equal(CoreProblems.EmployeeHasAccount, result.ProblemCode);
        Assert.Empty(_outbox.Messages);
    }

    [Fact]
    public async Task An_address_that_already_has_an_account_is_a_conflict()
    {
        var employee = Fake.Employee();
        Existing(employee);
        _users.Setup(s => s.EmailInUseAsync(employee.Email!, default)).ReturnsAsync(true);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new("member"));

        Assert.Equal(CoreProblems.EmailInUse, result.ProblemCode);
    }

    [Fact]
    public async Task An_unknown_employee_is_not_found()
    {
        var result = await Handler().Handle(
            Fake.Caller(), PublicId.New("emp").ToString(), new("member"));

        Assert.Equal(CommandOutcome.NotFound, result.Outcome);
    }
}
