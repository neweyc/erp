using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using Moq;
using static AppPlatform.Core.Features.Employees.GetEmployeesFeature;

namespace AppPlatform.Core.Tests;

public class GetEmployeesTests
{
    private readonly Mock<IEmployeeService> _employees = new();

    private GetEmployeesQueryHandler Handler() => new(_employees.Object);

    private void Returns(params Employee[] rows)
        => _employees.Setup(s => s.ListAsync(It.IsAny<bool>(), default)).ReturnsAsync([.. rows]);

    [Fact]
    public async Task Employees_are_projected_with_their_public_id_never_the_internal_key()
    {
        var employee = Fake.Employee();
        Returns(employee);

        var result = await Handler().Handle(Fake.Caller(), includeTerminated: false);

        var model = Assert.Single(Assert.IsType<List<EmployeeModel>>(result.Value));
        Assert.Equal(employee.PublicId, model.EmployeeId);
        Assert.Equal("Ada Lovelace", model.DisplayName);

        // The internal key must not appear anywhere in the response shape.
        Assert.DoesNotContain(
            employee.Id.ToString(),
            System.Text.Json.JsonSerializer.Serialize(result.Value),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Terminated_employees_are_excluded_by_default()
    {
        Returns();

        await Handler().Handle(Fake.Caller(), includeTerminated: false);

        // The flag is PASSED THROUGH, not ignored. A list endpoint that accepts a filter and
        // silently disregards it is one of the redshift QC findings this repo carries forward.
        _employees.Verify(s => s.ListAsync(false, default), Times.Once);
    }

    [Fact]
    public async Task Terminated_employees_are_included_on_request()
    {
        Returns();

        await Handler().Handle(Fake.Caller(), includeTerminated: true);

        _employees.Verify(s => s.ListAsync(true, default), Times.Once);
    }

    [Fact]
    public async Task Status_is_reported_so_a_caller_can_tell_terminated_from_active()
    {
        Returns(
            Fake.Employee(email: "a@x.com", status: EmployeeStatus.Active),
            Fake.Employee(email: "b@x.com", status: EmployeeStatus.Terminated),
            Fake.Employee(email: "c@x.com", status: EmployeeStatus.OnLeave));

        var result = await Handler().Handle(Fake.Caller(), includeTerminated: true);

        var models = Assert.IsType<List<EmployeeModel>>(result.Value);
        Assert.Equal(["Active", "Terminated", "OnLeave"], models.Select(m => m.Status));
    }

    [Fact]
    public async Task An_empty_roster_succeeds_with_an_empty_list()
    {
        Returns();

        var result = await Handler().Handle(Fake.Caller(), includeTerminated: false);

        // Not a 404 and not null: "this tenant has nobody yet" is an answer, and a null body
        // makes every caller write a null check the contract does not require.
        Assert.True(result.Succeeded);
        Assert.Empty(Assert.IsType<List<EmployeeModel>>(result.Value));
    }

    [Fact]
    public async Task An_employee_with_no_email_projects_a_null_rather_than_an_empty_string()
    {
        Returns(Fake.Employee(email: null!));

        var result = await Handler().Handle(Fake.Caller(), includeTerminated: false);

        var model = Assert.Single(Assert.IsType<List<EmployeeModel>>(result.Value));
        Assert.Null(model.Email);
    }
}
