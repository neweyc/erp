using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Time.Testing;
using Moq;
using static AppPlatform.Core.Features.Employees.TerminateEmployeeFeature;
using static AppPlatform.Core.Features.Employees.UpdateEmployeeFeature;
using static AppPlatform.Core.Features.Users.AddUserToRosterFeature;
using static AppPlatform.Core.Features.Users.GetUsersFeature;

namespace AppPlatform.Core.Tests;

/// <summary>
/// Editing and terminating employees, and putting an account on the roster (docs/permissions.md
/// §7). WHO may do each is enforced on the route; the lock is proven against real PostgreSQL in
/// privileges.tests.
/// </summary>
public class EmployeeRosterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly Mock<IEmployeeService> _employees = new();
    private readonly Mock<IUserService> _users = new();
    private readonly Mock<IRoleService> _roles = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly List<User> _everyone = [];
    private readonly List<Session> _sessions = [];
    private readonly Caller _admin = Fake.Caller();

    public EmployeeRosterTests()
    {
        _users.Setup(s => s.BeginTransactionAsync(default)).ReturnsAsync(Mock.Of<IDbContextTransaction>());
        _users.Setup(s => s.ListAsync(default)).ReturnsAsync(() => _everyone);
        _users.Setup(s => s.ListLiveSessionsAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((Guid id, CancellationToken _) => _sessions.Where(x => x.UserId == id && x.RevokedAt == null).ToList());
        _roles.Setup(s => s.ListAsync(default)).ReturnsAsync([]);
    }

    private Employee OnRoster(EmployeeStatus status = EmployeeStatus.Active, string email = "ada@example.com")
    {
        var employee = Fake.Employee(email: email, status: status);
        _employees.Setup(s => s.FindByPublicIdAsync(employee.PublicId, default)).ReturnsAsync(employee);
        return employee;
    }

    private User AccountFor(Employee? employee, string role = BuiltInRoles.CoreMember, Guid? id = null,
        UserStatus status = UserStatus.Active)
    {
        var user = new User
        {
            Id = id ?? Guid.NewGuid(),
            PublicId = PublicId.New("usr").ToString(),
            Email = employee?.Email ?? $"{Guid.NewGuid():N}@example.com",
            Roles = [role],
            Status = status,
            EmployeeId = employee?.Id,
            CompanyId = 1,
        };
        if (employee is not null)
            _users.Setup(s => s.FindByEmployeeAsync(employee.Id, default)).ReturnsAsync(user);
        _users.Setup(s => s.FindByPublicIdAsync(user.PublicId, default)).ReturnsAsync(user);
        _everyone.Add(user);
        return user;
    }

    /// <summary>The calling administrator, active, with their own employee record.</summary>
    private User Me() => AccountFor(OnRoster(email: "me@example.com"), BuiltInRoles.CoreAdmin, _admin.UserId);

    private Task<CommandResult> Update(Employee employee, string? first, string? last, string? email)
        => new UpdateEmployeeCommandHandler(_employees.Object, _users.Object, _outbox)
            .Handle(_admin, employee.PublicId, new UpdateEmployeeCommand(first, last, email));

    private Task<CommandResult> Terminate(Employee employee)
        => new TerminateEmployeeCommandHandler(_employees.Object, _users.Object, _roles.Object, _outbox, _clock)
            .Handle(_admin, employee.PublicId);

    private Task<CommandResult> AddToRoster(User user, string? first = "Grace", string? last = "Hopper")
        => new AddUserToRosterCommandHandler(_users.Object, _employees.Object, _outbox)
            .Handle(_admin, user.PublicId, new AddUserToRosterCommand(first, last));

    // ---- Editing ----

    [Fact]
    public async Task Editing_saves_the_cleaned_details_and_emits_an_event()
    {
        var employee = OnRoster();

        var result = await Update(employee, "  Ada ", " King ", " Ada.King@Example.com ");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(("Ada", "King", "ada.king@example.com"), (employee.FirstName, employee.LastName, employee.Email));
        var e = Assert.Single(_outbox.Events);
        Assert.Equal(("employee.updated", 2L), (e.EventType, e.AggregateVersion));
        _employees.Verify(s => s.SaveAsync(default), Times.Once);
        // Under the lock an invitation checks the address with, and termination writes under.
        _users.Verify(s => s.LockRoleChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task Editing_with_nothing_changed_saves_nothing_and_emits_nothing()
    {
        var employee = OnRoster();

        var result = await Update(employee, employee.FirstName, employee.LastName, employee.Email);

        Assert.True(result.Succeeded);
        Assert.Empty(_outbox.Events);
        _employees.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Editing_refuses_an_address_another_employee_has()
    {
        var employee = OnRoster();
        _employees.Setup(s => s.EmailInUseByAnotherAsync("grace@example.com", employee.Id, default)).ReturnsAsync(true);

        var result = await Update(employee, "Ada", "Lovelace", "grace@example.com");

        Assert.Equal(CoreProblems.EmailInUse, result.ProblemCode);
        Assert.Equal("ada@example.com", employee.Email);
    }

    [Fact]
    public async Task Editing_a_blank_email_removes_it()
    {
        var employee = OnRoster();

        await Update(employee, "Ada", "Lovelace", "  ");

        Assert.Null(employee.Email);
    }

    [Theory]
    [InlineData("", "Lovelace")]
    [InlineData("Ada", "  ")]
    public async Task Editing_requires_both_names(string first, string last)
    {
        var employee = OnRoster();

        var result = await Update(employee, first, last, null);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        _employees.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Editing_an_employees_email_does_not_change_their_sign_in_email()
    {
        var employee = OnRoster();
        var account = AccountFor(employee);

        await Update(employee, "Ada", "Lovelace", "new@example.com");

        // A sign-in address is a credential; the roster cannot move it (UpdateEmployeeFeature).
        Assert.Equal("ada@example.com", account.Email);
        _users.Verify(s => s.FindByEmployeeAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    // ---- Terminating ----

    [Fact]
    public async Task Terminating_ends_the_account_and_its_sessions_in_the_same_save()
    {
        Me();
        var employee = OnRoster();
        var account = AccountFor(employee);
        var live = new Session { UserId = account.Id };
        _sessions.Add(live);

        var result = await Terminate(employee);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(EmployeeStatus.Terminated, employee.Status);
        Assert.Equal(UserStatus.Deactivated, account.Status);
        Assert.Equal(Now, live.RevokedAt);
        Assert.Equal(["user.deactivated", "employee.terminated"], _outbox.Events.Select(e => e.EventType));
        _employees.Verify(s => s.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task Terminating_takes_the_tenants_access_lock_before_reading()
    {
        Me();
        var employee = OnRoster();
        var order = new List<string>();
        _users.Setup(s => s.LockRoleChangesAsync(default)).Callback(() => order.Add("lock")).Returns(Task.CompletedTask);
        _employees.Setup(s => s.FindByPublicIdAsync(employee.PublicId, default)).Callback(() => order.Add("read")).ReturnsAsync(employee);

        await Terminate(employee);

        Assert.Equal(["lock", "read"], order);
    }

    [Fact]
    public async Task Terminating_someone_with_no_account_only_records_the_termination()
    {
        var employee = OnRoster();

        var result = await Terminate(employee);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("employee.terminated", Assert.Single(_outbox.Events).EventType);
    }

    [Fact]
    public async Task Terminating_someone_whose_account_is_already_deactivated_leaves_the_account_alone()
    {
        var employee = OnRoster();
        AccountFor(employee, status: UserStatus.Deactivated);

        await Terminate(employee);

        Assert.Equal("employee.terminated", Assert.Single(_outbox.Events).EventType);
    }

    [Fact]
    public async Task You_cannot_terminate_yourself()
    {
        var me = Me();
        var myRecord = OnRoster(email: "me2@example.com");
        me.EmployeeId = myRecord.Id;
        _users.Setup(s => s.FindByEmployeeAsync(myRecord.Id, default)).ReturnsAsync(me);

        var result = await Terminate(myRecord);

        Assert.Equal(CoreProblems.CannotTerminateSelf, result.ProblemCode);
        Assert.Equal(EmployeeStatus.Active, myRecord.Status);
        Assert.Equal(UserStatus.Active, me.Status);
    }

    [Fact]
    public async Task The_last_active_administrator_cannot_be_terminated()
    {
        // The caller is not in this tenant's list (a support administrator, say).
        var employee = OnRoster();
        var onlyAdmin = AccountFor(employee, BuiltInRoles.CoreAdmin);

        var result = await Terminate(employee);

        Assert.Equal(CoreProblems.LastAdmin, result.ProblemCode);
        Assert.Equal(EmployeeStatus.Active, employee.Status);
        Assert.Equal(UserStatus.Active, onlyAdmin.Status);
        Assert.Empty(_outbox.Events);
    }

    [Fact]
    public async Task Terminating_twice_changes_nothing_the_second_time()
    {
        var employee = OnRoster(EmployeeStatus.Terminated);

        var result = await Terminate(employee);

        Assert.True(result.Succeeded);
        Assert.Empty(_outbox.Events);
        _employees.Verify(s => s.SaveAsync(default), Times.Never);
    }

    // ---- Putting an account on the roster ----

    [Fact]
    public async Task An_account_without_an_employee_is_given_one_from_its_own_email()
    {
        var user = AccountFor(null);
        Employee? added = null;
        _employees.Setup(s => s.Add(It.IsAny<Employee>())).Callback((Employee e) => added = e);

        var result = await AddToRoster(user);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(added);
        Assert.Equal(("Grace", "Hopper", user.Email), (added.FirstName, added.LastName, added.Email));
        Assert.Equal(added.Id, user.EmployeeId);
        // The account's history records the link too, numbered by its access version.
        Assert.Equal(["employee.created", "user.employee_linked"], _outbox.Events.Select(e => e.EventType));
        var linked = _outbox.Events[1];
        Assert.Equal((user.PublicId, 2L), (linked.AggregatePublicId, linked.AggregateVersion));
        Assert.Contains(added.PublicId, linked.Payload);
        _users.Verify(s => s.LockRoleChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task An_account_already_on_the_roster_is_refused()
    {
        var user = AccountFor(OnRoster());

        var result = await AddToRoster(user);

        Assert.Equal(CoreProblems.UserHasEmployee, result.ProblemCode);
    }

    [Fact]
    public async Task An_account_is_never_linked_to_another_employee_by_matching_its_email()
    {
        var user = AccountFor(null);
        _employees.Setup(s => s.EmailInUseAsync(user.Email, default)).ReturnsAsync(true);

        var result = await AddToRoster(user);

        Assert.Equal(CoreProblems.EmailInUse, result.ProblemCode);
        Assert.Null(user.EmployeeId);
        _employees.Verify(s => s.Add(It.IsAny<Employee>()), Times.Never);
    }

    // ---- The users list names each account's employee ----

    [Fact]
    public async Task The_users_list_gives_each_accounts_employee_and_null_for_one_not_on_the_roster()
    {
        var employee = OnRoster(EmployeeStatus.Terminated);
        var linked = AccountFor(employee, status: UserStatus.Deactivated);
        var unlinked = AccountFor(null);
        _employees.Setup(s => s.ListAsync(true, default)).ReturnsAsync([employee]);

        var result = await new GetUsersQueryHandler(_users.Object, _employees.Object, _clock).Handle(_admin);

        var rows = Assert.IsType<List<UserModel>>(result.Value);
        Assert.Equal(employee.PublicId, rows.Single(r => r.UserId == linked.PublicId).EmployeeId);
        Assert.Null(rows.Single(r => r.UserId == unlinked.PublicId).EmployeeId);
    }
}
