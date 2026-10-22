using AppPlatform.Api;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using static AppPlatform.Core.Features.Catalogs.CreateDepartmentFeature;
using static AppPlatform.Core.Features.Catalogs.CreateJobTitleFeature;
using static AppPlatform.Core.Features.Catalogs.CreateLocationFeature;
using static AppPlatform.Core.Features.Catalogs.GetDepartmentsFeature;
using static AppPlatform.Core.Features.Catalogs.UpdateDepartmentFeature;
using static AppPlatform.Core.Features.Catalogs.UpdateJobTitleFeature;
using static AppPlatform.Core.Features.Catalogs.UpdateLocationFeature;

namespace AppPlatform.Core.Tests;

/// <summary>
/// Departments, job titles and locations: the name rule, the department tree's rules, and that
/// every write takes the catalog lock and emits its event. The lock itself, against real
/// PostgreSQL, is in privileges.tests/CatalogTests.
/// </summary>
public class CatalogTests
{
    private readonly Mock<ICatalogService> _catalogs = new();
    private readonly Mock<IEmployeeService> _employees = new();
    private readonly RecordingOutbox _outbox = new();
    private readonly List<Department> _departments = [];
    private readonly List<JobTitle> _jobTitles = [];
    private readonly List<Location> _locations = [];

    public CatalogTests()
    {
        _catalogs.Setup(s => s.BeginTransactionAsync(default)).ReturnsAsync(Mock.Of<IDbContextTransaction>());
        _catalogs.Setup(s => s.ListDepartmentsAsync(default)).ReturnsAsync(() => _departments);
        _catalogs.Setup(s => s.ListJobTitlesAsync(default)).ReturnsAsync(() => _jobTitles);
        _catalogs.Setup(s => s.ListLocationsAsync(default)).ReturnsAsync(() => _locations);
        _catalogs.Setup(s => s.Add(It.IsAny<Department>())).Callback((Department d) => _departments.Add(d));
        _catalogs.Setup(s => s.Add(It.IsAny<JobTitle>())).Callback((JobTitle t) => _jobTitles.Add(t));
        _catalogs.Setup(s => s.Add(It.IsAny<Location>())).Callback((Location l) => _locations.Add(l));
        _employees.Setup(s => s.DefaultCompanyIdAsync(default)).ReturnsAsync(1);
    }

    private Department Dept(string name, Department? parent = null, bool active = true, int companyId = 1)
    {
        var d = new Department
        {
            PublicId = PublicId.New("dept").ToString(), TenantId = 1, CompanyId = companyId,
            Name = name, ParentId = parent?.Id, Active = active,
        };
        _departments.Add(d);
        return d;
    }

    private Task<CommandResult> Create(string? name, Department? parent = null)
        => new CreateDepartmentCommandHandler(_catalogs.Object, _employees.Object, _outbox)
            .Handle(Fake.Caller(), new CreateDepartmentCommand(name, parent?.PublicId));

    private Task<CommandResult> Update(Department d, string? name = null, Department? parent = null, bool? active = true)
        => new UpdateDepartmentCommandHandler(_catalogs.Object, _outbox)
            .Handle(Fake.Caller(), d.PublicId, new UpdateDepartmentCommand(name ?? d.Name, parent?.PublicId, active));

    // ---- Departments ----

    [Fact]
    public async Task A_department_is_added_under_the_lock_with_its_event()
    {
        var result = await Create("  Engineering ");

        Assert.True(result.Succeeded, result.Message);
        var added = Assert.Single(_departments);
        Assert.Equal(("Engineering", null), (added.Name, added.ParentId));
        Assert.StartsWith("dept_", added.PublicId, StringComparison.Ordinal);
        var e = Assert.Single(_outbox.Events);
        Assert.Equal(("department.created", added.PublicId, 1L), (e.EventType, e.AggregatePublicId, e.AggregateVersion));
        _catalogs.Verify(s => s.LockCatalogsAsync(default), Times.Once);
    }

    [Fact]
    public async Task A_sub_department_belongs_to_its_parents_company()
    {
        var parent = Dept("Engineering", companyId: 7);

        await Create("Platform", parent);

        var added = _departments.Single(d => d.Name == "Platform");
        Assert.Equal((parent.Id, 7), (added.ParentId, added.CompanyId));
    }

    [Fact]
    public async Task Siblings_cannot_share_a_name_ignoring_case_but_cousins_can()
    {
        var sales = Dept("Sales");
        var engineering = Dept("Engineering");
        Dept("Support", sales);

        Assert.Equal(CoreProblems.CatalogNameTaken, (await Create("support", sales)).ProblemCode);
        Assert.True((await Create("Support", engineering)).Succeeded);
        Assert.Equal(CoreProblems.CatalogNameTaken, (await Create("SALES")).ProblemCode);
    }

    [Fact]
    public async Task A_parent_from_elsewhere_is_not_found()
    {
        // The list is the tenant's own, so another tenant's id is simply absent from it.
        var result = await new CreateDepartmentCommandHandler(_catalogs.Object, _employees.Object, _outbox)
            .Handle(Fake.Caller(), new CreateDepartmentCommand("Platform", PublicId.New("dept").ToString()));

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Empty(_departments);
    }

    [Fact]
    public async Task Nothing_new_goes_under_an_inactive_department()
    {
        var retired = Dept("Old division", active: false);

        var result = await Create("Platform", retired);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Contains("Old division is inactive", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nesting_stops_at_the_maximum_depth()
    {
        Department? parent = null;
        for (var level = 1; level <= Department.MaxDepth; level++) parent = Dept($"Level {level}", parent);

        var result = await Create("Too deep", parent);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Contains($"at most {Department.MaxDepth} levels", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_a_branch_counts_its_own_depth()
    {
        // A three-level branch moved under a department six deep would reach nine.
        Department? deep = null;
        for (var level = 1; level <= 6; level++) deep = Dept($"Deep {level}", deep);
        var branch = Dept("Branch");
        var middle = Dept("Middle", branch);
        Dept("Leaf", middle);

        var result = await Update(branch, parent: deep);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Null(branch.ParentId);
    }

    [Fact]
    public async Task A_department_cannot_be_moved_inside_itself_or_its_own_sub_department()
    {
        var engineering = Dept("Engineering");
        var platform = Dept("Platform", engineering);
        var tools = Dept("Tools", platform);

        foreach (var target in new[] { engineering, platform, tools })
        {
            var result = await Update(engineering, parent: target);
            Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        }

        Assert.Null(engineering.ParentId);
        Assert.Empty(_outbox.Events);
    }

    [Fact]
    public async Task A_department_with_active_sub_departments_cannot_be_deactivated()
    {
        var engineering = Dept("Engineering");
        var platform = Dept("Platform", engineering);

        Assert.Equal(CoreProblems.ValidationFailed, (await Update(engineering, active: false)).ProblemCode);
        Assert.True(engineering.Active);

        // Once the sub-department is retired, the parent can be.
        Assert.True((await Update(platform, parent: engineering, active: false)).Succeeded);
        Assert.True((await Update(engineering, active: false)).Succeeded);
        Assert.False(engineering.Active);
    }

    [Fact]
    public async Task A_department_cannot_be_reactivated_under_an_inactive_parent()
    {
        var retired = Dept("Old division", active: false);
        var child = Dept("Team", retired, active: false);

        var result = await Update(child, parent: retired, active: true);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.False(child.Active);
    }

    [Fact]
    public async Task Renaming_advances_the_version_and_emits_an_event_and_no_change_emits_nothing()
    {
        var sales = Dept("Sales");

        Assert.True((await Update(sales, name: "Sales")).Succeeded);
        Assert.Empty(_outbox.Events);
        _catalogs.Verify(s => s.SaveAsync(default), Times.Never);

        Assert.True((await Update(sales, name: "Sales and marketing")).Succeeded);
        Assert.Equal("Sales and marketing", sales.Name);
        var e = Assert.Single(_outbox.Events);
        Assert.Equal(("department.updated", 2L), (e.EventType, e.AggregateVersion));
    }

    [Fact]
    public async Task An_edit_must_say_whether_the_department_is_active()
    {
        var sales = Dept("Sales");

        var result = await Update(sales, active: null);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.True(sales.Active);
    }

    [Fact]
    public async Task An_id_of_the_wrong_kind_is_not_a_department()
    {
        var result = await new UpdateDepartmentCommandHandler(_catalogs.Object, _outbox)
            .Handle(Fake.Caller(), PublicId.New("loc").ToString(), new UpdateDepartmentCommand("X", null, true));

        Assert.Equal(CommandOutcome.NotFound, result.Outcome);
        _catalogs.Verify(s => s.ListDepartmentsAsync(default), Times.Never);
    }

    [Fact]
    public async Task Departments_are_listed_as_a_tree_with_paths()
    {
        var sales = Dept("Sales");
        var engineering = Dept("Engineering");
        Dept("Tools", Dept("Platform", engineering));
        Dept("Accounts", sales);

        var result = await new GetDepartmentsQueryHandler(_catalogs.Object).Handle(Fake.Caller());

        var rows = Assert.IsType<List<DepartmentModel>>(result.Value);
        Assert.Equal(
            ["Engineering", "Engineering › Platform", "Engineering › Platform › Tools", "Sales", "Sales › Accounts"],
            rows.Select(r => r.Path));
        Assert.Equal([1, 2, 3, 1, 2], rows.Select(r => r.Depth));
        Assert.Equal(engineering.PublicId, rows[1].ParentId);
    }

    // ---- Job titles and locations ----

    [Fact]
    public async Task A_job_title_is_added_and_a_duplicate_refused_even_when_retired()
    {
        var create = new CreateJobTitleCommandHandler(_catalogs.Object, _outbox);

        Assert.True((await create.Handle(Fake.Caller(), new CreateJobTitleCommand(" Engineer "))).Succeeded);
        _jobTitles.Single().Active = false;
        var again = await create.Handle(Fake.Caller(), new CreateJobTitleCommand("ENGINEER"));

        Assert.Equal(CoreProblems.CatalogNameTaken, again.ProblemCode);
        Assert.Equal("Engineer", Assert.Single(_jobTitles).Name);
        Assert.Equal("job_title.created", Assert.Single(_outbox.Events).EventType);
        _catalogs.Verify(s => s.LockCatalogsAsync(default), Times.Exactly(2));
    }

    [Fact]
    public async Task A_job_title_is_renamed_and_deactivated_in_one_edit()
    {
        var title = new JobTitle { PublicId = PublicId.New("title").ToString(), Name = "Engineer" };
        _jobTitles.Add(title);

        var result = await new UpdateJobTitleCommandHandler(_catalogs.Object, _outbox)
            .Handle(Fake.Caller(), title.PublicId, new UpdateJobTitleCommand("Software engineer", false));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(("Software engineer", false, 2L), (title.Name, title.Active, title.Version));
        Assert.Equal("job_title.updated", Assert.Single(_outbox.Events).EventType);
    }

    [Fact]
    public async Task A_location_belongs_to_the_company_and_cannot_take_another_locations_name()
    {
        var create = new CreateLocationCommandHandler(_catalogs.Object, _employees.Object, _outbox);
        await create.Handle(Fake.Caller(), new CreateLocationCommand("Head office"));
        await create.Handle(Fake.Caller(), new CreateLocationCommand("Leeds depot"));
        var leeds = _locations.Single(l => l.Name == "Leeds depot");

        var rename = await new UpdateLocationCommandHandler(_catalogs.Object, _outbox)
            .Handle(Fake.Caller(), leeds.PublicId, new UpdateLocationCommand("head office", true));

        Assert.Equal(CoreProblems.CatalogNameTaken, rename.ProblemCode);
        Assert.All(_locations, l => Assert.Equal(1, l.CompanyId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_name_is_required(string? name)
    {
        var result = await new CreateLocationCommandHandler(_catalogs.Object, _employees.Object, _outbox)
            .Handle(Fake.Caller(), new CreateLocationCommand(name));

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        _catalogs.Verify(s => s.LockCatalogsAsync(default), Times.Never);
    }

    [Fact]
    public async Task An_over_long_name_names_the_limit()
    {
        var result = await new CreateJobTitleCommandHandler(_catalogs.Object, _outbox)
            .Handle(Fake.Caller(), new CreateJobTitleCommand(new string('x', JobTitle.NameMaxLength + 1)));

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
        Assert.Contains($"{JobTitle.NameMaxLength} characters", result.Message, StringComparison.Ordinal);
    }
}
