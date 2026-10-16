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
/// The rules for changing roles (docs/permissions.md §6). WHO may change them is enforced on the
/// route; the last-administrator lock is proven against real PostgreSQL in privileges.tests.
/// </summary>
public class ChangeUserRolesTests
{
    private readonly Mock<IUserService> _users = new();
    private readonly RecordingOutbox _outbox = new();

    public ChangeUserRolesTests()
    {
        _users.Setup(s => s.BeginTransactionAsync(default)).ReturnsAsync(Mock.Of<IDbContextTransaction>());
    }

    private static Caller Admin() => Fake.Caller() with { LicensedApps = ["tickets", "ledger"] };

    private User Existing(string role = "member", params string[] appRoles)
    {
        var user = new User
        {
            PublicId = PublicId.New("usr").ToString(),
            Email = "grace@example.com",
            Role = role,
            AppRoles = appRoles,
            Status = UserStatus.Active,
        };
        _users.Setup(s => s.FindByPublicIdAsync(user.PublicId, default)).ReturnsAsync(user);
        return user;
    }

    private Task<CommandResult> Change(
        User user, string? role = null, Dictionary<string, string?>? appRoles = null, Caller? caller = null)
        => new ChangeUserRolesCommandHandler(_users.Object, _outbox)
            .Handle(caller ?? Admin(), user.PublicId, new(role, appRoles));

    [Fact]
    public async Task Granting_an_app_role_saves_it_advances_the_version_and_emits_an_event()
    {
        var user = Existing();

        var result = await Change(user, appRoles: new() { ["tickets"] = "agent" });

        Assert.True(result.Succeeded);
        Assert.Equal(["tickets:agent"], user.AppRoles);
        Assert.Equal(2, user.AccessVersion);
        var e = Assert.Single(_outbox.Events);
        Assert.Equal(("user.roles_changed", 2L), (e.EventType, e.AggregateVersion));
        _users.Verify(s => s.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task An_app_left_out_of_the_request_keeps_its_role()
    {
        // The Users page shows licensed apps only; it must not erase a role in an unlicensed one.
        var user = Existing("member", "ledger:controller");

        await Change(user, appRoles: new() { ["tickets"] = "requester" });

        Assert.Equal(["ledger:controller", "tickets:requester"], user.AppRoles);
    }

    [Fact]
    public async Task Null_removes_an_app_role_even_when_the_app_is_unlicensed()
    {
        var user = Existing("member", "ledger:controller", "tickets:agent");

        var result = await Change(user, appRoles: new() { ["ledger"] = null },
            caller: Fake.Caller() with { LicensedApps = ["tickets"] });

        Assert.True(result.Succeeded);
        Assert.Equal(["tickets:agent"], user.AppRoles);
    }

    [Fact]
    public async Task Granting_a_role_in_an_unlicensed_app_is_refused()
    {
        var user = Existing();

        var result = await Change(user, appRoles: new() { ["ledger"] = "viewer" },
            caller: Fake.Caller() with { LicensedApps = ["tickets"] });

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Empty(user.AppRoles);
    }

    [Theory]
    [InlineData("owner", null, null)]
    [InlineData(null, "tickets", "overlord")]
    [InlineData(null, "payroll", "clerk")]
    [InlineData(null, "core", "admin")]
    public async Task Names_outside_the_catalogue_are_refused(string? role, string? app, string? appRole)
    {
        var user = Existing();
        var appRoles = app is null ? null : new Dictionary<string, string?> { [app] = appRole };

        var result = await Change(user, role, appRoles);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Equal("member", user.Role);
        _users.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task The_only_active_admin_cannot_be_demoted()
    {
        var user = Existing("admin");
        _users.Setup(s => s.LockActiveAdminsAsync(default)).ReturnsAsync([user.Id]);

        var result = await Change(user, role: "manager");

        Assert.Equal(CoreProblems.LastAdmin, result.ProblemCode);
        Assert.Equal("admin", user.Role);
        Assert.Empty(_outbox.Events);
        _users.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task An_admin_can_be_demoted_while_another_remains()
    {
        var user = Existing("admin");
        _users.Setup(s => s.LockActiveAdminsAsync(default)).ReturnsAsync([user.Id, Guid.NewGuid()]);

        var result = await Change(user, role: "member");

        Assert.True(result.Succeeded);
        Assert.Equal("member", user.Role);
    }

    [Fact]
    public async Task The_role_change_lock_is_taken_before_anything_is_read()
    {
        // Reading first would let a change work from state the change before it is replacing.
        // The concurrency itself is proven against real PostgreSQL in privileges.tests.
        var order = new List<string>();
        var user = Existing();
        _users.Setup(s => s.LockRoleChangesAsync(default)).Callback(() => order.Add("lock"));
        _users.Setup(s => s.LockActiveAdminsAsync(default)).Callback(() => order.Add("admins")).ReturnsAsync([]);
        _users.Setup(s => s.FindByPublicIdAsync(user.PublicId, default)).Callback(() => order.Add("user")).ReturnsAsync(user);

        await Change(user, appRoles: new() { ["tickets"] = "agent" });

        Assert.Equal(["lock", "admins", "user"], order);
    }

    [Fact]
    public async Task A_change_to_the_same_roles_saves_nothing()
    {
        // Saving would emit an event for a change that never happened and end the user's session.
        var user = Existing("member", "tickets:agent");

        var result = await Change(user, role: "member", appRoles: new() { ["tickets"] = "agent" });

        Assert.True(result.Succeeded);
        Assert.Equal(1, user.AccessVersion);
        Assert.Empty(_outbox.Events);
        _users.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task A_user_the_lookup_cannot_find_is_not_found()
    {
        // Another tenant's user is one such: the filtered lookup finds nothing for it. That the
        // real lookup is filtered is proven against PostgreSQL in privileges.tests.
        var stranger = new User { PublicId = PublicId.New("usr").ToString(), Email = "x@y.z", Role = "member" };

        var result = await Change(stranger, role: "admin");

        Assert.Equal(CoreProblems.NotFound, result.ProblemCode);
    }
}
