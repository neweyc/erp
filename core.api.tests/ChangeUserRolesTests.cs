using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using static AppPlatform.Core.Features.Users.ChangeUserRolesFeature;

namespace AppPlatform.Core.Tests;

/// <summary>
/// The rules for giving and taking roles (docs/permissions.md §6). WHO may do it is enforced on the
/// route; the tenant lock and the races it prevents are proven against real PostgreSQL in
/// privileges.tests.
/// </summary>
public class ChangeUserRolesTests
{
    private readonly Mock<IUserService> _users = new();
    private readonly Mock<IRoleService> _roles = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly List<User> _everyone = [];
    private readonly List<Role> _customRoles = [];

    public ChangeUserRolesTests()
    {
        _users.Setup(s => s.BeginTransactionAsync(default)).ReturnsAsync(Mock.Of<IDbContextTransaction>());
        _users.Setup(s => s.ListAsync(default)).ReturnsAsync(() => _everyone);
        _roles.Setup(s => s.ListAsync(default)).ReturnsAsync(() => _customRoles);
    }

    private static Caller Admin() => Fake.Caller() with { LicensedApps = ["tickets", "ledger"] };

    private User Existing(UserStatus status = UserStatus.Active, params string[] roles)
    {
        var user = new User
        {
            PublicId = PublicId.New("usr").ToString(),
            Email = $"{Guid.NewGuid():N}@example.com",
            Roles = roles.Length == 0 ? [BuiltInRoles.CoreMember] : roles,
            Status = status,
        };
        _users.Setup(s => s.FindByPublicIdAsync(user.PublicId, default)).ReturnsAsync(user);
        _everyone.Add(user);
        return user;
    }

    private Role Custom(string name, params string[] permissions)
    {
        var role = new Role { PublicId = PublicId.New("rol").ToString(), Name = name, Permissions = permissions };
        _customRoles.Add(role);
        return role;
    }

    private Task<CommandResult> Change(
        User user, string[]? add = null, string[]? remove = null, Caller? caller = null)
        => new ChangeUserRolesCommandHandler(_users.Object, _roles.Object, _outbox)
            .Handle(caller ?? Admin(), user.PublicId, new(add?.ToList<string?>(), remove?.ToList<string?>()));

    // ---- Giving and taking ----

    [Fact]
    public async Task Adding_a_role_saves_it_advances_the_version_and_emits_an_event()
    {
        Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);
        var user = Existing();

        var result = await Change(user, add: ["tickets:agent"]);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["core:member", "tickets:agent"], user.Roles);
        Assert.Equal(2, user.AccessVersion);
        var e = Assert.Single(_outbox.Events);
        Assert.Equal(("user.roles_changed", 2L), (e.EventType, e.AggregateVersion));
        _users.Verify(s => s.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task A_user_can_hold_several_roles_at_once()
    {
        var user = Existing();
        var clerk = Custom("Clerk", Permissions.Ledger.Read);

        await Change(user, add: ["tickets:requester", "ledger:viewer", clerk.PublicId]);

        Assert.Equal(["core:member", "ledger:viewer", clerk.PublicId, "tickets:requester"], user.Roles);
    }

    [Fact]
    public async Task Roles_not_named_are_left_alone()
    {
        // The page lists licensed apps only; it must not erase a role held in an unlicensed one.
        var user = Existing(UserStatus.Active, BuiltInRoles.CoreMember, "ledger:controller");

        await Change(user, add: ["tickets:requester"], caller: Fake.Caller() with { LicensedApps = ["tickets"] });

        Assert.Equal(["core:member", "ledger:controller", "tickets:requester"], user.Roles);
    }

    [Fact]
    public async Task A_role_can_be_removed_even_when_its_app_is_unlicensed()
    {
        var user = Existing(UserStatus.Active, BuiltInRoles.CoreMember, "ledger:controller");

        var result = await Change(user, remove: ["ledger:controller"],
            caller: Fake.Caller() with { LicensedApps = ["tickets"] });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["core:member"], user.Roles);
    }

    [Fact]
    public async Task A_change_that_changes_nothing_saves_nothing()
    {
        // Saving would emit an event for a change that never happened and end the user's session.
        var user = Existing(UserStatus.Active, BuiltInRoles.CoreMember, "tickets:agent");

        var result = await Change(user, add: ["tickets:agent"], remove: ["ledger:viewer"]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, user.AccessVersion);
        Assert.Empty(_outbox.Events);
        _users.Verify(s => s.SaveAsync(default), Times.Never);
    }

    // ---- What may be given ----

    [Fact]
    public async Task A_built_in_role_in_an_unlicensed_app_cannot_be_given()
    {
        var user = Existing();

        var result = await Change(user, add: ["ledger:viewer"], caller: Fake.Caller() with { LicensedApps = ["tickets"] });

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Equal(["core:member"], user.Roles);
    }

    [Theory]
    [InlineData("core.owner")]  // no such built-in role
    [InlineData("admin")]       // Cycle 12's name, not a key
    [InlineData("rol_unknown")] // not one of this tenant's roles: another tenant's id looks exactly like this
    public async Task Keys_that_are_not_roles_are_refused(string key)
    {
        var user = Existing();

        var result = await Change(user, add: [key]);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        _users.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Naming_no_role_or_one_role_both_ways_is_refused()
    {
        var user = Existing();

        Assert.Equal(CoreProblems.ValidationFailed, (await Change(user)).ProblemCode);
        Assert.Equal(CoreProblems.ValidationFailed,
            (await Change(user, add: ["tickets:agent"], remove: ["tickets:agent"])).ProblemCode);
    }

    [Fact]
    public async Task A_null_key_in_the_request_is_a_validation_failure_not_a_crash()
    {
        var user = Existing();

        var result = await new ChangeUserRolesCommandHandler(_users.Object, _roles.Object, _outbox)
            .Handle(Admin(), user.PublicId, new([null], null));

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
    }

    [Fact]
    public async Task A_stored_null_key_is_skipped_and_not_written_back()
    {
        // text[] allows NULL elements, written by hand or by an older script. Another user holding
        // one must not break the last-administrator count either.
        Existing(UserStatus.Active, [null!, BuiltInRoles.CoreAdmin]);
        var user = Existing(UserStatus.Active, [null!, BuiltInRoles.CoreMember]);

        var result = await Change(user, add: ["tickets:agent"]);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["core:member", "tickets:agent"], user.Roles);
    }

    // ---- The tenant keeps someone who can manage users ----

    [Fact]
    public async Task The_only_active_admin_cannot_lose_the_role()
    {
        var user = Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);

        var result = await Change(user, add: ["core:manager"], remove: [BuiltInRoles.CoreAdmin]);

        Assert.Equal(CoreProblems.LastAdmin, result.ProblemCode);
        Assert.Equal([BuiltInRoles.CoreAdmin], user.Roles);
        Assert.Empty(_outbox.Events);
    }

    [Fact]
    public async Task An_admin_can_lose_the_role_while_another_active_one_remains()
    {
        Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);
        var user = Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);

        var result = await Change(user, remove: [BuiltInRoles.CoreAdmin]);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task An_invited_admin_does_not_count()
    {
        // They cannot sign in, so they cannot manage anyone yet.
        Existing(UserStatus.Invited, BuiltInRoles.CoreAdmin);
        var user = Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);

        var result = await Change(user, remove: [BuiltInRoles.CoreAdmin]);

        Assert.Equal(CoreProblems.LastAdmin, result.ProblemCode);
    }

    [Fact]
    public async Task A_custom_role_granting_user_management_counts()
    {
        // The rule is about the permission, not the built-in role's name.
        var security = Custom("Security", Permissions.Core.UserManage);
        Existing(UserStatus.Active, BuiltInRoles.CoreMember, security.PublicId);
        var user = Existing(UserStatus.Active, BuiltInRoles.CoreAdmin);

        var result = await Change(user, remove: [BuiltInRoles.CoreAdmin]);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task An_admin_can_drop_the_built_in_role_while_keeping_the_permission_through_another()
    {
        var security = Custom("Security", Permissions.Core.UserManage);
        var user = Existing(UserStatus.Active, BuiltInRoles.CoreAdmin, security.PublicId);

        var result = await Change(user, remove: [BuiltInRoles.CoreAdmin]);

        Assert.True(result.Succeeded, result.Message);
    }

    // ---- Order of work ----

    [Fact]
    public async Task The_role_change_lock_is_taken_before_anything_is_read()
    {
        // Reading first would let a change work from state the change ahead of it is replacing.
        var order = new List<string>();
        var user = Existing();
        _users.Setup(s => s.LockRoleChangesAsync(default)).Callback(() => order.Add("lock")).Returns(Task.CompletedTask);
        _roles.Setup(s => s.ListAsync(default)).Callback(() => order.Add("roles")).ReturnsAsync(() => _customRoles);
        _users.Setup(s => s.FindByPublicIdAsync(user.PublicId, default)).Callback(() => order.Add("user")).ReturnsAsync(user);

        await Change(user, add: ["tickets:agent"]);

        Assert.Equal(["lock", "roles", "user"], order);
    }

    [Fact]
    public async Task A_user_the_lookup_cannot_find_is_not_found()
    {
        // Another tenant's user is one such: the filtered lookup finds nothing for it. That the
        // real lookup is filtered is proven against PostgreSQL in privileges.tests.
        var stranger = new User { PublicId = PublicId.New("usr").ToString(), Email = "x@y.z" };

        var result = await Change(stranger, add: [BuiltInRoles.CoreAdmin]);

        Assert.Equal(CoreProblems.NotFound, result.ProblemCode);
    }
}
