using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Time.Testing;
using Moq;
using static AppPlatform.Core.Features.Users.DeactivateUserFeature;
using static AppPlatform.Core.Features.Users.GetUsersFeature;
using static AppPlatform.Core.Features.Users.ReactivateUserFeature;
using static AppPlatform.Core.Features.Users.ResendInvitationFeature;

namespace AppPlatform.Core.Tests;

/// <summary>
/// Deactivating, reactivating and re-inviting a user (docs/permissions.md §6). WHO may do it is
/// enforced on the route; the lock and the race with accepting an invitation are proven against
/// real PostgreSQL in privileges.tests.
/// </summary>
public class UserLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly Mock<IUserService> _users = new();
    private readonly Mock<IRoleService> _roles = new();
    private readonly Mock<IEmployeeService> _employees = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly List<User> _everyone = [];
    private readonly List<Session> _sessions = [];
    private readonly List<UserToken> _tokens = [];
    private readonly List<InvitationEmail> _emails = [];
    private readonly Caller _admin = Fake.Caller();

    public UserLifecycleTests()
    {
        _users.Setup(s => s.BeginTransactionAsync(default)).ReturnsAsync(Mock.Of<IDbContextTransaction>());
        _users.Setup(s => s.ListAsync(default)).ReturnsAsync(() => _everyone);
        _users.Setup(s => s.ListLiveSessionsAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((Guid id, CancellationToken _) => _sessions.Where(x => x.UserId == id && x.RevokedAt == null).ToList());
        _users.Setup(s => s.ListInvitationTokensAsync(It.IsAny<IReadOnlyCollection<Guid>>(), default)).ReturnsAsync(() => _tokens);
        _users.Setup(s => s.ListInvitationEmailsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), default))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => _emails.Where(e => ids.Contains(e.MessageId)).ToList());
        _roles.Setup(s => s.ListAsync(default)).ReturnsAsync([]);
        _employees.Setup(s => s.ListAsync(true, default)).ReturnsAsync([]);
    }

    private User Existing(UserStatus status = UserStatus.Active, string role = BuiltInRoles.CoreMember, Guid? id = null)
    {
        var user = new User
        {
            Id = id ?? Guid.NewGuid(),
            PublicId = PublicId.New("usr").ToString(),
            Email = $"{Guid.NewGuid():N}@example.com",
            Roles = [role],
            Status = status,
            PasswordHash = status == UserStatus.Invited ? null : "hash",
        };
        _users.Setup(s => s.FindByPublicIdAsync(user.PublicId, default)).ReturnsAsync(user);
        _everyone.Add(user);
        return user;
    }

    /// <summary>The calling administrator's own account, active and able to manage users.</summary>
    private User Me() => Existing(UserStatus.Active, BuiltInRoles.CoreAdmin, _admin.UserId);

    private Task<CommandResult> Deactivate(User user)
        => new DeactivateUserCommandHandler(_users.Object, _roles.Object, _outbox, _clock).Handle(_admin, user.PublicId);

    private Task<CommandResult> Reactivate(User user)
        => new ReactivateUserCommandHandler(_users.Object, _employees.Object, _outbox).Handle(_admin, user.PublicId);

    private Task<CommandResult> Resend(User user)
        => new ResendInvitationCommandHandler(_users.Object, _employees.Object, _outbox, Fake.Tenants(), _clock).Handle(_admin, user.PublicId);

    private void LinkTo(User user, EmployeeStatus status)
    {
        var employee = Fake.Employee(status: status);
        user.EmployeeId = employee.Id;
        _employees.Setup(s => s.FindByIdAsync(employee.Id, default)).ReturnsAsync(employee);
    }

    // ---- Deactivating ----

    [Fact]
    public async Task Deactivating_stops_the_account_revokes_its_sessions_and_emits_an_event()
    {
        Me();
        var user = Existing();
        var live = new Session { UserId = user.Id };
        var alreadyRevoked = new Session { UserId = user.Id, RevokedAt = Now.AddDays(-1) };
        _sessions.AddRange([live, alreadyRevoked]);

        var result = await Deactivate(user);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(UserStatus.Deactivated, user.Status);
        // Revoked, not just refused: if the account is reactivated, an old cookie stays dead.
        Assert.Equal(Now, live.RevokedAt);
        Assert.Equal(Now.AddDays(-1), alreadyRevoked.RevokedAt);
        // Roles are kept, so reactivating restores exactly what the person had.
        Assert.Equal([BuiltInRoles.CoreMember], user.Roles);
        var e = Assert.Single(_outbox.Events);
        Assert.Equal(("user.deactivated", 2L), (e.EventType, e.AggregateVersion));
        _users.Verify(s => s.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task Deactivating_takes_the_tenants_access_lock_before_reading()
    {
        Me();
        var user = Existing();
        var order = new List<string>();
        _users.Setup(s => s.LockRoleChangesAsync(default)).Callback(() => order.Add("lock")).Returns(Task.CompletedTask);
        _users.Setup(s => s.FindByPublicIdAsync(user.PublicId, default)).Callback(() => order.Add("read")).ReturnsAsync(user);

        await Deactivate(user);

        Assert.Equal(["lock", "read"], order);
    }

    [Fact]
    public async Task The_last_active_person_who_can_manage_users_cannot_be_deactivated()
    {
        // The caller's own account is not in this tenant's list here (a support administrator,
        // say), so the target is the only active one. An invited administrator does not count.
        var onlyAdmin = Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);
        Existing(UserStatus.Invited, BuiltInRoles.CoreAdmin);

        var result = await Deactivate(onlyAdmin);

        Assert.Equal(CoreProblems.LastAdmin, result.ProblemCode);
        Assert.Equal(UserStatus.Active, onlyAdmin.Status);
    }

    [Fact]
    public async Task One_of_two_administrators_can_be_deactivated()
    {
        Me();
        var other = Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);

        Assert.True((await Deactivate(other)).Succeeded);
    }

    [Fact]
    public async Task An_administrator_cannot_deactivate_their_own_account()
    {
        var me = Me();
        Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);

        var result = await Deactivate(me);

        Assert.Equal(CoreProblems.CannotDeactivateSelf, result.ProblemCode);
        Assert.Equal(UserStatus.Active, me.Status);
    }

    [Fact]
    public async Task Deactivating_an_invited_account_is_allowed_and_kills_its_invitation()
    {
        Me();
        var invited = Existing(UserStatus.Invited);

        Assert.True((await Deactivate(invited)).Succeeded);
        // AcceptInviteFeature accepts only an invited account (AuthTests).
        Assert.Equal(UserStatus.Deactivated, invited.Status);
    }

    [Fact]
    public async Task Deactivating_twice_changes_nothing_the_second_time()
    {
        Me();
        var user = Existing(UserStatus.Deactivated);

        var result = await Deactivate(user);

        Assert.True(result.Succeeded);
        Assert.Empty(_outbox.Events);
        _users.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Deactivating_an_unknown_user_is_not_found()
    {
        var result = await new DeactivateUserCommandHandler(_users.Object, _roles.Object, _outbox, _clock)
            .Handle(_admin, PublicId.New("usr").ToString());

        Assert.Equal(CoreProblems.NotFound, result.ProblemCode);
    }

    // ---- Reactivating ----

    [Fact]
    public async Task Reactivating_restores_an_account_that_had_a_password_to_active()
    {
        var user = Existing(UserStatus.Deactivated);

        var result = await Reactivate(user);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(UserStatus.Active, user.Status);
        var e = Assert.Single(_outbox.Events);
        Assert.Equal(("user.reactivated", 2L), (e.EventType, e.AggregateVersion));
    }

    [Fact]
    public async Task Reactivating_an_account_that_never_accepted_makes_it_invited_again()
    {
        var user = Existing(UserStatus.Deactivated);
        user.PasswordHash = null;

        await Reactivate(user);

        // No password, so there is nothing it could sign in with yet.
        Assert.Equal(UserStatus.Invited, user.Status);
    }

    [Fact]
    public async Task A_terminated_employees_account_cannot_be_reactivated()
    {
        var user = Existing(UserStatus.Deactivated);
        LinkTo(user, EmployeeStatus.Terminated);

        var result = await Reactivate(user);

        Assert.Equal(CoreProblems.EmployeeTerminated, result.ProblemCode);
        Assert.Equal(UserStatus.Deactivated, user.Status);
    }

    [Fact]
    public async Task Reactivating_an_active_account_changes_nothing()
    {
        var user = Existing();

        Assert.True((await Reactivate(user)).Succeeded);
        Assert.Empty(_outbox.Events);
        _users.Verify(s => s.SaveAsync(default), Times.Never);
    }

    // ---- Resending an invitation ----

    [Fact]
    public async Task Resending_issues_a_new_link_and_queues_the_email_in_one_save()
    {
        var user = Existing(UserStatus.Invited);
        UserToken? issued = null;
        _users.Setup(s => s.AddToken(It.IsAny<UserToken>())).Callback((UserToken t) => issued = t);

        var result = await Resend(user);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(issued);
        Assert.Equal((user.Id, TokenPurpose.Invite, Now.Add(InvitationToken.Lifetime)), (issued.UserId, issued.Purpose, issued.ExpiresAt));
        var message = Assert.Single(_outbox.Messages);
        Assert.Equal((OutboxTransports.Email, user.Email), (message.Transport, message.Destination));
        Assert.Contains("\"kind\":\"invite\"", message.Payload);
        // The link remembers its email, so the Users page reports that email's delivery.
        Assert.Equal(message.Id, issued.MessageId);
        // The email carries the plaintext; the row only its hash.
        Assert.DoesNotContain(issued.TokenHash, message.Payload);
        _users.Verify(s => s.SaveAsync(default), Times.Once);
    }

    [Theory]
    [InlineData(UserStatus.Active)]
    [InlineData(UserStatus.Deactivated)]
    public async Task Only_an_invited_account_can_be_sent_another_invitation(UserStatus status)
    {
        var user = Existing(status);

        var result = await Resend(user);

        Assert.Equal(CoreProblems.NotInvited, result.ProblemCode);
        Assert.Empty(_outbox.Messages);
        _users.Verify(s => s.AddToken(It.IsAny<UserToken>()), Times.Never);
    }

    [Fact]
    public async Task A_terminated_employee_is_not_sent_another_invitation()
    {
        var user = Existing(UserStatus.Invited);
        LinkTo(user, EmployeeStatus.Terminated);

        Assert.Equal(CoreProblems.EmployeeTerminated, (await Resend(user)).ProblemCode);
        Assert.Empty(_outbox.Messages);
    }

    // ---- What the Users page shows about an invitation ----

    private async Task<UserModel> Listed(User user)
    {
        var result = await new GetUsersQueryHandler(_users.Object, _employees.Object, _clock).Handle(_admin);
        return Assert.IsType<List<UserModel>>(result.Value).Single(u => u.UserId == user.PublicId);
    }

    /// <summary>An invitation issued at <paramref name="issuedAt"/>; a null delivery means its email is no longer in the outbox.</summary>
    private void Invitation(User user, DateTimeOffset issuedAt, OutboxStatus? delivery)
    {
        var messageId = Guid.NewGuid();
        _tokens.Add(new UserToken
        {
            UserId = user.Id, Purpose = TokenPurpose.Invite, TokenHash = "h", MessageId = messageId,
            CreatedAt = issuedAt, ExpiresAt = issuedAt.Add(InvitationToken.Lifetime),
        });
        if (delivery is { } status) _emails.Add(new InvitationEmail(messageId, status));
    }

    [Theory]
    [InlineData(OutboxStatus.Pending, "pending")]
    [InlineData(OutboxStatus.Succeeded, "delivered")]
    [InlineData(OutboxStatus.Dead, "failed")]
    [InlineData(null, "unknown")]
    public async Task An_invited_account_shows_how_its_invitation_email_is_doing(OutboxStatus? status, string expected)
    {
        var user = Existing(UserStatus.Invited);
        Invitation(user, Now.AddHours(-1), status);

        var invitation = (await Listed(user)).Invitation;

        Assert.NotNull(invitation);
        Assert.Equal((expected, false), (invitation.Delivery, invitation.Expired));
    }

    [Fact]
    public async Task Only_the_newest_invitation_is_described()
    {
        var user = Existing(UserStatus.Invited);
        Invitation(user, Now.AddDays(-8), OutboxStatus.Dead);
        Invitation(user, Now.AddMinutes(-5), OutboxStatus.Succeeded);

        var invitation = (await Listed(user)).Invitation!;

        // The first email failed and its link has expired; the resend arrived and is live.
        Assert.Equal(("delivered", false, Now.AddMinutes(-5).Add(InvitationToken.Lifetime)),
            (invitation.Delivery, invitation.Expired, invitation.ExpiresAt));
    }

    [Fact]
    public async Task A_pruned_email_is_unknown_even_when_an_older_failed_one_remains()
    {
        var user = Existing(UserStatus.Invited);
        // The outbox keeps failed messages for 90 days and delivered ones for 7, so the older
        // failure can outlive the newer delivery. Matching by address would report "failed".
        Invitation(user, Now.AddDays(-9), OutboxStatus.Dead);
        Invitation(user, Now.AddDays(-1), delivery: null);

        Assert.Equal("unknown", (await Listed(user)).Invitation!.Delivery);
    }

    [Fact]
    public async Task An_expired_link_is_reported_as_expired()
    {
        var user = Existing(UserStatus.Invited);
        Invitation(user, Now.AddDays(-8), OutboxStatus.Succeeded);

        Assert.True((await Listed(user)).Invitation!.Expired);
    }

    [Fact]
    public async Task An_account_that_is_not_invited_shows_no_invitation()
    {
        var user = Existing();
        Invitation(user, Now.AddDays(-1), OutboxStatus.Succeeded);

        Assert.Null((await Listed(user)).Invitation);
    }
}
