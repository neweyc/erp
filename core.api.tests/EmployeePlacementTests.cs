using AppPlatform.Api;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using static AppPlatform.Core.Features.Employees.CreateEmployeeFeature;
using static AppPlatform.Core.Features.Employees.GetEmployeesFeature;
using static AppPlatform.Core.Features.Employees.UpdateEmployeeFeature;

namespace AppPlatform.Core.Tests;

/// <summary>
/// Placing an employee in a department, job title and location (EmployeePlacement), through the
/// add and edit features that share it, and how the roster shows it.
/// </summary>
public class EmployeePlacementTests
{
    private readonly Mock<IEmployeeService> _employees = new();
    private readonly Mock<IUserService> _users = new();
    private readonly Mock<ICatalogService> _catalogs = new();
    private readonly RecordingOutbox _outbox = new();

    private readonly Department _engineering = new() { PublicId = PublicId.New("dept").ToString(), Name = "Engineering" };
    private readonly Department _platform;
    private readonly Department _retired = new() { PublicId = PublicId.New("dept").ToString(), Name = "Old division", Active = false };
    private readonly JobTitle _engineer = new() { PublicId = PublicId.New("title").ToString(), Name = "Engineer" };
    private readonly Location _office = new() { PublicId = PublicId.New("loc").ToString(), Name = "Head office" };

    public EmployeePlacementTests()
    {
        _platform = new() { PublicId = PublicId.New("dept").ToString(), Name = "Platform", ParentId = _engineering.Id };
        _employees.Setup(s => s.DefaultCompanyIdAsync(default)).ReturnsAsync(1);
        _users.Setup(s => s.BeginTransactionAsync(default)).ReturnsAsync(Mock.Of<IDbContextTransaction>());

        foreach (var d in new[] { _engineering, _platform, _retired })
            _catalogs.Setup(s => s.FindDepartmentAsync(d.PublicId, default)).ReturnsAsync(d);
        _catalogs.Setup(s => s.FindJobTitleAsync(_engineer.PublicId, default)).ReturnsAsync(_engineer);
        _catalogs.Setup(s => s.FindLocationAsync(_office.PublicId, default)).ReturnsAsync(_office);
        _catalogs.Setup(s => s.ListDepartmentsAsync(default)).ReturnsAsync([_engineering, _platform, _retired]);
        _catalogs.Setup(s => s.ListJobTitlesAsync(default)).ReturnsAsync([_engineer]);
        _catalogs.Setup(s => s.ListLocationsAsync(default)).ReturnsAsync([_office]);
    }

    private async Task<(CommandResult Result, Employee? Added)> Add(string? department, string? jobTitle = null, string? location = null)
    {
        Employee? added = null;
        _employees.Setup(s => s.Add(It.IsAny<Employee>())).Callback((Employee e) => added = e);
        var result = await new CreateEmployeeCommandHandler(_employees.Object, _catalogs.Object, _outbox)
            .Handle(Fake.Caller(), new CreateEmployeeCommand("Grace", "Hopper", null, department, jobTitle, location));
        return (result, added);
    }

    private Task<CommandResult> Edit(Employee employee, string? department, string? jobTitle = null, string? location = null)
    {
        _employees.Setup(s => s.FindByPublicIdAsync(employee.PublicId, default)).ReturnsAsync(employee);
        return new UpdateEmployeeCommandHandler(_employees.Object, _users.Object, _catalogs.Object, _outbox)
            .Handle(Fake.Caller(), employee.PublicId,
                new UpdateEmployeeCommand(employee.FirstName, employee.LastName, employee.Email, department, jobTitle, location));
    }

    [Fact]
    public async Task An_employee_is_added_with_a_department_job_title_and_location()
    {
        var (result, added) = await Add(_platform.PublicId, _engineer.PublicId, _office.PublicId);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal((_platform.Id, _engineer.Id, _office.Id), (added!.DepartmentId, added.JobTitleId, added.LocationId));
    }

    [Fact]
    public async Task Every_placement_is_optional()
    {
        var (result, added) = await Add(null, "", "  ");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal((null, null, null), (added!.DepartmentId, added.JobTitleId, added.LocationId));
    }

    [Fact]
    public async Task An_id_of_the_wrong_kind_is_refused_before_any_lookup()
    {
        // A location id in the department field finds nothing, and the mistake should say so.
        var (result, added) = await Add(_office.PublicId);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Equal("That is not a department id.", result.Message);
        Assert.Null(added);
        _catalogs.Verify(s => s.FindDepartmentAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task A_department_this_tenant_does_not_have_is_refused()
    {
        // Another tenant's department is filtered out of the lookup, so it arrives here as not found.
        var (result, added) = await Add(PublicId.New("dept").ToString());

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Equal("No such department.", result.Message);
        Assert.Null(added);
    }

    [Fact]
    public async Task Nobody_new_is_placed_in_a_retired_department()
    {
        var (result, _) = await Add(_retired.PublicId);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Contains("Old division is no longer in use", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Someone_already_in_a_retired_department_keeps_it_through_an_edit()
    {
        var employee = Fake.Employee();
        employee.DepartmentId = _retired.Id;
        employee.FirstName = "Augusta";

        var result = await Edit(employee, _retired.PublicId);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(_retired.Id, employee.DepartmentId);
    }

    [Fact]
    public async Task An_edit_moves_the_employee_and_emits_one_event()
    {
        var employee = Fake.Employee();

        var result = await Edit(employee, _engineering.PublicId, _engineer.PublicId);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal((_engineering.Id, _engineer.Id, (Guid?)null), (employee.DepartmentId, employee.JobTitleId, employee.LocationId));
        Assert.Equal("employee.updated", Assert.Single(_outbox.Events).EventType);
    }

    [Fact]
    public async Task An_edit_that_leaves_the_placement_as_it_was_saves_nothing()
    {
        var employee = Fake.Employee();
        employee.DepartmentId = _platform.Id;

        var result = await Edit(employee, _platform.PublicId);

        Assert.True(result.Succeeded);
        Assert.Empty(_outbox.Events);
        _employees.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task The_roster_shows_the_department_by_its_path_and_says_when_it_is_retired()
    {
        var placed = Fake.Employee();
        placed.DepartmentId = _platform.Id;
        placed.JobTitleId = _engineer.Id;
        var retired = Fake.Employee(email: "old@example.com");
        retired.DepartmentId = _retired.Id;
        _employees.Setup(s => s.ListAsync(false, default)).ReturnsAsync([placed, retired]);

        var result = await new GetEmployeesQueryHandler(_employees.Object, _catalogs.Object)
            .Handle(Fake.Caller(), includeTerminated: false);

        var rows = Assert.IsType<List<EmployeeModel>>(result.Value);
        Assert.Equal(new PlacementModel(_platform.PublicId, "Engineering › Platform", true), rows[0].Department);
        Assert.Equal(new PlacementModel(_engineer.PublicId, "Engineer", true), rows[0].JobTitle);
        Assert.Null(rows[0].Location);
        Assert.Equal(new PlacementModel(_retired.PublicId, "Old division", false), rows[1].Department);
    }
}
