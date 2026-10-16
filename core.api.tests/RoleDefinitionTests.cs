using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Roles;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using static AppPlatform.Core.Features.Roles.RoleRules;

namespace AppPlatform.Core.Tests;

/// <summary>
/// Creating, editing and deleting the tenant's own roles (docs/permissions.md §6). Concurrency is
/// proven against real PostgreSQL in privileges.tests.
/// </summary>
public class RoleDefinitionTests
{
    private readonly Mock<IUserService> _users = new();
    private readonly Mock<IRoleService> _roles = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly List<User> _everyone = [];
    private readonly List<Role> _customRoles = [];

    public RoleDefinitionTests()
    {
        _users.Setup(s => s.BeginTransactionAsync(default)).ReturnsAsync(Mock.Of<IDbContextTransaction>());
        _users.Setup(s => s.ListAsync(default)).ReturnsAsync(() => _everyone);
        _roles.Setup(s => s.ListAsync(default)).ReturnsAsync(() => _customRoles);
        _roles.Setup(s => s.FindByPublicIdAsync(It.IsAny<string>(), default))
            .ReturnsAsync((string id, CancellationToken _) => _customRoles.FirstOrDefault(r => r.PublicId == id));
    }

    private static Caller Admin(params string[] licensed)
        => Fake.Caller() with { LicensedApps = licensed.Length == 0 ? ["tickets", "ledger"] : licensed };

    private Role Custom(string name, params string[] permissions)
    {
        var role = new Role { PublicId = PublicId.New("rol").ToString(), Name = name, Permissions = permissions };
        _customRoles.Add(role);
        return role;
    }

    private User Holder(UserStatus status, params string[] roles)
    {
        var user = new User { PublicId = PublicId.New("usr").ToString(), Email = $"{Guid.NewGuid():N}@x.test", Roles = roles, Status = status };
        _everyone.Add(user);
        return user;
    }

    private Task<CommandResult> Create(string? name, string[] permissions, Caller? caller = null, string? description = null)
        => new CreateRoleFeature.CreateRoleCommandHandler(_users.Object, _roles.Object, _outbox)
            .Handle(caller ?? Admin(), new RoleDefinitionCommand(name, description, permissions.ToList<string?>()));

    private Task<CommandResult> Update(Role role, string name, string[] permissions, Caller? caller = null)
        => new UpdateRoleFeature.UpdateRoleCommandHandler(_users.Object, _roles.Object, _outbox)
            .Handle(caller ?? Admin(), role.PublicId, new RoleDefinitionCommand(name, role.Description, permissions.ToList<string?>()));

    private Task<CommandResult> Delete(string rolePublicId)
        => new DeleteRoleFeature.DeleteRoleCommandHandler(_users.Object, _roles.Object, _outbox)
            .Handle(Admin(), rolePublicId);

    // ---- Creating ----

    [Fact]
    public async Task Creating_a_role_saves_it_sorted_and_emits_an_event()
    {
        Role? added = null;
        _roles.Setup(s => s.Add(It.IsAny<Role>())).Callback<Role>(r => added = r);

        var result = await Create("  Finance clerk ", [Permissions.Ledger.EntryPost, Permissions.Core.EmployeeRead, Permissions.Ledger.Read],
            description: "Posts entries.");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Finance clerk", added!.Name);
        Assert.Equal([Permissions.Core.EmployeeRead, Permissions.Ledger.EntryPost, Permissions.Ledger.Read], added.Permissions);
        Assert.StartsWith("rol_", added.PublicId, StringComparison.Ordinal);
        var e = Assert.Single(_outbox.Events);
        Assert.Equal(("role.created", 1L), (e.EventType, e.AggregateVersion));
        _users.Verify(s => s.SaveAsync(default), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Agent")]         // a built-in role's name
    [InlineData("administrator")] // ignoring case
    public async Task A_name_must_be_present_and_not_a_built_in_roles(string name)
    {
        var result = await Create(name, [Permissions.Core.EmployeeRead]);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        _roles.Verify(s => s.Add(It.IsAny<Role>()), Times.Never);
    }

    [Fact]
    public async Task A_name_longer_than_the_column_is_refused()
        => Assert.Equal(CoreProblems.ValidationFailed,
            (await Create(new string('x', Role.NameMaxLength + 1), [Permissions.Core.EmployeeRead])).ProblemCode);

    [Fact]
    public async Task A_name_already_used_ignoring_case_is_a_conflict()
    {
        Custom("Finance clerk", Permissions.Ledger.Read);

        var result = await Create("FINANCE CLERK", [Permissions.Ledger.Read]);

        Assert.Equal(CoreProblems.RoleNameTaken, result.ProblemCode);
    }

    [Theory]
    [InlineData("tickets.ticket.delete")] // not a permission
    [InlineData("core.employee")]        // nor is a prefix of one
    public async Task Unknown_permissions_are_refused(string permission)
        => Assert.Equal(CoreProblems.ValidationFailed, (await Create("Odd", [permission])).ProblemCode);

    [Fact]
    public async Task A_role_needs_at_least_one_permission()
        => Assert.Equal(CoreProblems.ValidationFailed, (await Create("Empty", [])).ProblemCode);

    [Fact]
    public async Task A_permission_in_an_unlicensed_app_cannot_be_added()
    {
        var result = await Create("Books", [Permissions.Ledger.Read], caller: Admin("tickets"));

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
    }

    [Fact]
    public async Task Roles_and_their_writes_are_validated_under_the_lock()
    {
        var order = new List<string>();
        _users.Setup(s => s.LockRoleChangesAsync(default)).Callback(() => order.Add("lock")).Returns(Task.CompletedTask);
        _roles.Setup(s => s.ListAsync(default)).Callback(() => order.Add("roles")).ReturnsAsync(() => _customRoles);

        await Create("Clerk", [Permissions.Core.EmployeeRead]);

        Assert.Equal(["lock", "roles"], order);
    }

    // ---- Editing ----

    [Fact]
    public async Task Editing_replaces_the_definition_and_advances_the_version()
    {
        var role = Custom("Clerk", Permissions.Ledger.Read);

        var result = await Update(role, "Senior clerk", [Permissions.Ledger.Read, Permissions.Ledger.EntryPost]);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Senior clerk", role.Name);
        Assert.Equal([Permissions.Ledger.EntryPost, Permissions.Ledger.Read], role.Permissions);
        Assert.Equal(2, role.Version);
        Assert.Equal(("role.updated", 2L), (_outbox.Events.Single().EventType, _outbox.Events.Single().AggregateVersion));
    }

    [Fact]
    public async Task A_role_may_keep_its_own_name_with_different_case()
    {
        var role = Custom("Clerk", Permissions.Ledger.Read);

        var result = await Update(role, "clerk", [Permissions.Ledger.Read]);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task Permissions_held_in_an_unlicensed_app_survive_an_edit_that_cannot_show_them()
    {
        var role = Custom("Mixed", Permissions.Ledger.Read, Permissions.Tickets.TicketRead);

        await Update(role, "Mixed", [Permissions.Tickets.TicketRead, Permissions.Tickets.TicketClose], caller: Admin("tickets"));

        Assert.Equal([Permissions.Ledger.Read, Permissions.Tickets.TicketClose, Permissions.Tickets.TicketRead], role.Permissions);
    }

    [Fact]
    public async Task A_role_whose_permissions_are_all_in_an_unlicensed_app_can_still_be_renamed()
    {
        // The editor shows licensed apps only, so it sends no permissions at all; the role keeps its
        // own and is not empty.
        var role = Custom("Books", Permissions.Ledger.Read);

        var result = await Update(role, "Old books", [], caller: Admin("tickets"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Old books", role.Name);
        Assert.Equal([Permissions.Ledger.Read], role.Permissions);
    }

    [Fact]
    public async Task A_stored_null_permission_does_not_stop_the_role_being_edited()
    {
        var role = Custom("Clerk", null!, Permissions.Ledger.Read);

        var result = await Update(role, "Clerk", [Permissions.Ledger.Read, Permissions.Ledger.EntryPost], caller: Admin("tickets", "ledger"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([Permissions.Ledger.EntryPost, Permissions.Ledger.Read], role.Permissions);
    }

    [Fact]
    public async Task A_null_permission_is_a_validation_failure_not_a_crash()
        => Assert.Equal(CoreProblems.ValidationFailed,
            (await new CreateRoleFeature.CreateRoleCommandHandler(_users.Object, _roles.Object, _outbox)
                .Handle(Admin(), new RoleDefinitionCommand("Odd", null, [null]))).ProblemCode);

    [Fact]
    public async Task An_edit_that_changes_nothing_saves_nothing()
    {
        // Saving would sign out everyone who holds the role, for nothing.
        var role = Custom("Clerk", Permissions.Ledger.Read);

        var result = await Update(role, "Clerk", [Permissions.Ledger.Read]);

        Assert.True(result.Succeeded);
        Assert.Empty(_outbox.Events);
        _users.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Taking_user_management_out_of_the_only_role_granting_it_is_refused()
    {
        var security = Custom("Security", Permissions.Core.UserManage, Permissions.Core.UserRead);
        Holder(UserStatus.Active, BuiltInRoles.CoreMember, security.PublicId);

        var result = await Update(security, "Security", [Permissions.Core.UserRead]);

        Assert.Equal(CoreProblems.LastAdmin, result.ProblemCode);
        Assert.Contains(Permissions.Core.UserManage, security.Permissions);
    }

    [Fact]
    public async Task Taking_user_management_out_of_a_role_is_fine_while_an_admin_remains()
    {
        var security = Custom("Security", Permissions.Core.UserManage);
        Holder(UserStatus.Active, BuiltInRoles.CoreMember, security.PublicId);
        Holder(UserStatus.Active, BuiltInRoles.CoreAdmin);

        var result = await Update(security, "Security", [Permissions.Core.UserRead]);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task A_built_in_role_cannot_be_edited()
    {
        var result = await new UpdateRoleFeature.UpdateRoleCommandHandler(_users.Object, _roles.Object, _outbox)
            .Handle(Admin(), "core:admin", new RoleDefinitionCommand("Admin", "", [Permissions.Core.EmployeeRead]));

        Assert.Equal(CoreProblems.NotFound, result.ProblemCode);
    }

    // ---- Deleting ----

    [Fact]
    public async Task An_unheld_role_is_deleted_with_an_event()
    {
        var role = Custom("Clerk", Permissions.Ledger.Read);

        var result = await Delete(role.PublicId);

        Assert.True(result.Succeeded, result.Message);
        _roles.Verify(s => s.Remove(role), Times.Once);
        Assert.Equal(("role.deleted", 2L), (_outbox.Events.Single().EventType, _outbox.Events.Single().AggregateVersion));
    }

    [Fact]
    public async Task A_held_role_cannot_be_deleted_even_by_an_invited_holder()
    {
        // Deleting it would change that person's access silently, the day they accept.
        var role = Custom("Clerk", Permissions.Ledger.Read);
        Holder(UserStatus.Invited, BuiltInRoles.CoreMember, role.PublicId);

        var result = await Delete(role.PublicId);

        Assert.Equal(CoreProblems.RoleInUse, result.ProblemCode);
        _roles.Verify(s => s.Remove(It.IsAny<Role>()), Times.Never);
    }

    [Fact]
    public async Task Deleting_a_role_that_does_not_exist_is_not_found()
        => Assert.Equal(CoreProblems.NotFound, (await Delete(PublicId.New("rol").ToString())).ProblemCode);
}
