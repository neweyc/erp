using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Time.Testing;
using Moq;
using System.Text.Json;
using static AppPlatform.Core.Features.Employees.InviteEmployeeFeature;

namespace AppPlatform.Core.Tests;

public class InviteEmployeeTests
{
    private readonly Mock<IEmployeeService> _employees = new();
    private readonly Mock<IUserService> _users = new();
    private readonly Mock<IRoleService> _roles = new();
    private readonly RecordingOutbox _outbox = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private InviteEmployeeCommandHandler Handler()
        => new(_employees.Object, _users.Object, _roles.Object, _outbox, Fake.Tenants(), new FakeTimeProvider(Now));

    public InviteEmployeeTests()
    {
        _users.Setup(s => s.BeginTransactionAsync(default)).ReturnsAsync(Mock.Of<IDbContextTransaction>());
        _roles.Setup(s => s.ListAsync(default)).ReturnsAsync([]);
    }

    private void Existing(Employee employee)
        => _employees.Setup(s => s.FindByPublicIdAsync(employee.PublicId, default)).ReturnsAsync(employee);

    [Fact]
    public async Task Inviting_an_employee_creates_an_invited_account_linked_by_id()
    {
        var employee = Fake.Employee();
        Existing(employee);
        User? added = null;
        _users.Setup(s => s.Add(It.IsAny<User>())).Callback<User>(u => added = u);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:manager"], employee.Email));

        Assert.True(result.Succeeded);
        Assert.NotNull(added);
        Assert.Equal(employee.Id, added.EmployeeId);
        Assert.Equal(UserStatus.Invited, added.Status);
        Assert.Equal(["core:manager"], added.Roles);
    }

    [Fact]
    public async Task The_invitation_email_is_staged_and_commits_with_the_account()
    {
        var employee = Fake.Employee();
        Existing(employee);

        await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], employee.Email));

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

        await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], employee.Email));

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

        // The link the email carries, on the tenant's own address: the shell there accepts it and
        // leaves the person on the page they sign in from.
        Assert.Equal(
            $"{Fake.SignInUrl}/accept-invite?token={Uri.EscapeDataString(plaintext)}",
            payload.GetProperty("acceptUrl").GetString());
    }

    // Who may invite (core.user.manage, admins only) is enforced on the route: see
    // PermissionTests in packages/auth.tests and the coverage check in boundary.tests.

    [Fact]
    public async Task A_refused_invitation_issues_no_token()
    {
        var employee = Fake.Employee(status: EmployeeStatus.Terminated);
        Existing(employee);

        await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], employee.Email));

        _users.Verify(s => s.AddToken(It.IsAny<UserToken>()), Times.Never);
    }

    [Fact]
    public async Task An_id_of_the_wrong_kind_is_refused_before_any_lookup()
    {
        // A ticket id here would otherwise find no employee and return 404, hiding the real
        // mistake: two kinds of id sharing one route parameter.
        var result = await Handler().Handle(Fake.Caller(), PublicId.New("tkt").ToString(), new(["core:member"], "ada@example.test"));

        Assert.Equal(CommandOutcome.NotFound, result.Outcome);
        _employees.Verify(s => s.FindByPublicIdAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task An_invitation_approved_for_an_address_that_has_since_changed_is_refused()
    {
        // An administrator opened "Give access" seeing ada@…; a Manager then edited the address.
        // Sending anyway would deliver the access the administrator chose to the new address.
        var employee = Fake.Employee();
        Existing(employee);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:admin"], "someone.else@example.test"));

        Assert.Equal(CoreProblems.EmployeeChanged, result.ProblemCode);
        _users.Verify(s => s.Add(It.IsAny<User>()), Times.Never);
        Assert.Empty(_outbox.Messages);
    }

    [Fact]
    public async Task The_address_seen_is_compared_ignoring_case_and_spaces()
    {
        var employee = Fake.Employee();
        Existing(employee);

        var result = await Handler().Handle(
            Fake.Caller(), employee.PublicId, new(["core:member"], $"  {employee.Email!.ToUpperInvariant()} "));

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task An_invitation_must_say_which_address_it_was_approved_for()
    {
        var employee = Fake.Employee();
        Existing(employee);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], null));

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Empty(_outbox.Messages);
    }

    [Fact]
    public async Task A_terminated_employee_cannot_be_invited()
    {
        var employee = Fake.Employee(status: EmployeeStatus.Terminated);
        Existing(employee);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], employee.Email));

        Assert.Equal(CoreProblems.EmployeeTerminated, result.ProblemCode);
        Assert.Empty(_outbox.Messages);
    }

    [Fact]
    public async Task An_employee_with_no_email_cannot_be_invited()
    {
        var employee = Fake.Employee(email: null!);
        // The address the inviter saw has since been removed.
        Existing(employee);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], "ada@example.com"));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.Contains("nowhere to send", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_employee_who_already_has_an_account_is_a_conflict()
    {
        var employee = Fake.Employee();
        Existing(employee);
        _users.Setup(s => s.FindByEmployeeAsync(employee.Id, default))
            .ReturnsAsync(new User { Email = employee.Email!, Roles = [BuiltInRoles.CoreMember] });

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], employee.Email));

        Assert.Equal(CoreProblems.EmployeeHasAccount, result.ProblemCode);
        Assert.Empty(_outbox.Messages);
    }

    [Fact]
    public async Task An_address_that_already_has_an_account_is_a_conflict()
    {
        var employee = Fake.Employee();
        Existing(employee);
        _users.Setup(s => s.EmailInUseAsync(employee.Email!, default)).ReturnsAsync(true);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], employee.Email));

        Assert.Equal(CoreProblems.EmailInUse, result.ProblemCode);
    }

    [Fact]
    public async Task An_unknown_employee_is_not_found()
    {
        var result = await Handler().Handle(
            Fake.Caller(), PublicId.New("emp").ToString(), new(["core:member"], "ada@example.test"));

        Assert.Equal(CommandOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task With_no_roles_named_the_account_is_a_member()
    {
        var employee = Fake.Employee();
        Existing(employee);
        User? added = null;
        _users.Setup(s => s.Add(It.IsAny<User>())).Callback<User>(u => added = u);

        await Handler().Handle(Fake.Caller(), employee.PublicId, new(null, employee.Email));

        Assert.Equal([BuiltInRoles.CoreMember], added!.Roles);
    }

    [Fact]
    public async Task An_invitation_can_give_several_roles_including_a_custom_one()
    {
        var employee = Fake.Employee();
        Existing(employee);
        _roles.Setup(s => s.ListAsync(default)).ReturnsAsync([new Role { PublicId = "rol_clerk", Name = "Clerk" }]);
        User? added = null;
        _users.Setup(s => s.Add(It.IsAny<User>())).Callback<User>(u => added = u);

        var result = await Handler().Handle(
            Fake.Caller() with { LicensedApps = ["tickets"] }, employee.PublicId,
            new(["tickets:requester", "rol_clerk", "core:member"], employee.Email));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["core:member", "rol_clerk", "tickets:requester"], added!.Roles);
    }

    [Theory]
    [InlineData("core.owner")]        // not a role
    [InlineData("rol_unknown")]       // not one of this tenant's roles
    [InlineData("ledger:controller")] // an app the tenant has not licensed
    public async Task An_invitation_cannot_give_what_the_Users_page_would_refuse(string key)
    {
        var employee = Fake.Employee();
        Existing(employee);

        var result = await Handler().Handle(
            Fake.Caller() with { LicensedApps = ["tickets"] }, employee.PublicId, new([key], employee.Email));

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        _users.Verify(s => s.Add(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task A_null_role_key_is_a_validation_failure_not_a_crash()
    {
        var employee = Fake.Employee();
        Existing(employee);

        var result = await Handler().Handle(Fake.Caller(), employee.PublicId, new([null], employee.Email));

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
    }

    [Fact]
    public async Task Inviting_takes_the_role_change_lock_before_reading_roles()
    {
        // So a custom role named here cannot be deleted before the account is saved holding it.
        var order = new List<string>();
        var employee = Fake.Employee();
        Existing(employee);
        _users.Setup(s => s.LockRoleChangesAsync(default)).Callback(() => order.Add("lock")).Returns(Task.CompletedTask);
        _roles.Setup(s => s.ListAsync(default)).Callback(() => order.Add("roles")).ReturnsAsync([]);

        await Handler().Handle(Fake.Caller(), employee.PublicId, new(["core:member"], employee.Email));

        Assert.Equal(["lock", "roles"], order);
    }
}
