using AppPlatform.Api;
using AppPlatform.Audit;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Catalogs;
using AppPlatform.Core.Features.Employees;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Departments, job titles and locations against real PostgreSQL, as core's runtime role: catalog
/// writes in a tenant run one at a time under the catalog lock, so a loop or a duplicate cannot be
/// made by two requests at once; the database itself refuses a cross-tenant placement or parent and
/// a duplicate name; and an app sees the placement through core_v1.employee.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class CatalogTests(PrivilegeFixture fixture)
{
    private static readonly TimeSpan BlockedFor = TimeSpan.FromMilliseconds(500);
    private const string ForeignKeyViolation = "23503";
    private const string UniqueViolation = "23505";

    private sealed record Seeded(int TenantId, int CompanyId, Guid Admin);

    private async Task<Seeded> SeedAsync()
    {
        await using var connection = await OpenAsync();
        var tenantId = (int)(await Scalar(connection,
            "INSERT INTO platform.tenant (public_id, name, status, created_at) " +
            $"VALUES ('{PublicId.New("ten")}', 'Catalogs {Guid.NewGuid():N}', 'Active', now()) RETURNING id"))!;
        var companyId = (int)(await Scalar(connection,
            "INSERT INTO core.company (tenant_id, public_id, name, active) " +
            $"VALUES ({tenantId}, '{PublicId.New("co")}', 'Catalogs Ltd', true) RETURNING id"))!;
        return new Seeded(tenantId, companyId, Guid.CreateVersion7());
    }

    private async Task<(Guid Id, string PublicId)> AddDepartmentAsync(Seeded seeded, string name, Guid? parent = null)
    {
        var id = Guid.CreateVersion7();
        var publicId = PublicId.New("dept").ToString();
        await fixture.ExecuteAsync(
            "INSERT INTO core.department (id, tenant_id, company_id, public_id, name, parent_id, active, version) " +
            $"VALUES ('{id}', {seeded.TenantId}, {seeded.CompanyId}, '{publicId}', '{name}', " +
            $"{(parent is { } p ? $"'{p}'" : "NULL")}, true, 1)");
        return (id, publicId);
    }

    private async Task<(Guid Id, string PublicId)> AddEmployeeAsync(Seeded seeded)
    {
        var id = Guid.CreateVersion7();
        var publicId = PublicId.New("emp").ToString();
        await fixture.ExecuteAsync(
            "INSERT INTO core.employee (id, tenant_id, company_id, public_id, first_name, last_name, email, status, version, deleted) " +
            $"VALUES ('{id}', {seeded.TenantId}, {seeded.CompanyId}, '{publicId}', 'Ada', 'Lovelace', '{Guid.NewGuid():N}@catalogs.test', 'Active', 1, false)");
        return (id, publicId);
    }

    private CoreDbContext Core(Seeded seeded) => new(
        new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(fixture.ConnectionStringAs("ap_core_rt"))
            .UseSnakeCaseNamingConvention().Options,
        Tenant(seeded.TenantId), new AmbientAuditActor(AuditActor.User(seeded.Admin)), TimeProvider.System);

    private static AmbientTenantProvider Tenant(int tenantId)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);
        return tenant;
    }

    private static Caller Admin(Seeded seeded) => new()
    {
        PrincipalId = seeded.Admin, Kind = PrincipalKind.User, UserId = seeded.Admin,
        TenantId = seeded.TenantId, CompanyId = seeded.CompanyId, Roles = [BuiltInRoles.CoreAdmin], LicensedApps = [],
    };

    /// <summary>Moves a department under another (or to the top), through the real handler, in its own context.</summary>
    private async Task<CommandResult> MoveAsync(Seeded seeded, string departmentPublicId, string name, string? parentPublicId)
    {
        await using var db = Core(seeded);
        return await new UpdateDepartmentFeature.UpdateDepartmentCommandHandler(
                new EFCatalogService(db, Tenant(seeded.TenantId)), new Outbox.Outbox(db, TimeProvider.System))
            .Handle(Admin(seeded), departmentPublicId, new(name, parentPublicId, true));
    }

    private async Task<CommandResult> CreateJobTitleAsync(Seeded seeded, string name)
    {
        await using var db = Core(seeded);
        return await new CreateJobTitleFeature.CreateJobTitleCommandHandler(
                new EFCatalogService(db, Tenant(seeded.TenantId)), new Outbox.Outbox(db, TimeProvider.System))
            .Handle(Admin(seeded), new(name));
    }

    [Fact]
    public async Task A_catalog_write_waits_for_the_catalog_lock()
    {
        var seeded = await SeedAsync();
        var (_, sales) = await AddDepartmentAsync(seeded, "Sales");

        await using var held = await OpenAsync();
        await using var heldTransaction = await held.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@key))", held))
        {
            lockCommand.Parameters.AddWithValue("key", EFCatalogService.CatalogLockKey(seeded.TenantId));
            await lockCommand.ExecuteScalarAsync();
        }

        var rename = MoveAsync(seeded, sales, "Sales and marketing", null);
        await Task.Delay(BlockedFor);
        Assert.False(rename.IsCompleted, "a catalog write should wait for the catalog lock");

        await heldTransaction.RollbackAsync();
        Assert.True((await rename).Succeeded);
    }

    [Fact]
    public async Task Two_departments_moved_under_each_other_at_once_cannot_make_a_loop()
    {
        var seeded = await SeedAsync();
        var (a, aPublic) = await AddDepartmentAsync(seeded, "A");
        var (b, bPublic) = await AddDepartmentAsync(seeded, "B");

        var results = await Task.WhenAll(MoveAsync(seeded, aPublic, "A", bPublic), MoveAsync(seeded, bPublic, "B", aPublic));

        // Each alone is fine; the second, seeing the first committed, would close a loop.
        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.ProblemCode == CoreProblems.ValidationFailed);
        await using var check = await OpenAsync();
        Assert.Equal(1L, await Scalar(check,
            $"SELECT count(*) FROM core.department WHERE id IN ('{a}', '{b}') AND parent_id IS NOT NULL"));
    }

    [Fact]
    public async Task Two_job_titles_of_the_same_name_added_at_once_make_one()
    {
        var seeded = await SeedAsync();

        var results = await Task.WhenAll(CreateJobTitleAsync(seeded, "Engineer"), CreateJobTitleAsync(seeded, "engineer"));

        // The second waits, then sees the first: a clean 409, not a unique-index 500.
        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.ProblemCode == CoreProblems.CatalogNameTaken);
    }

    [Fact]
    public async Task The_database_refuses_a_duplicate_name_that_skips_the_handler()
    {
        var seeded = await SeedAsync();
        var (sales, _) = await AddDepartmentAsync(seeded, "Sales");
        await AddDepartmentAsync(seeded, "Support", sales);

        var sibling = await Assert.ThrowsAsync<PostgresException>(() => AddDepartmentAsync(seeded, "SUPPORT", sales));
        Assert.Equal(UniqueViolation, sibling.SqlState);
        var topLevel = await Assert.ThrowsAsync<PostgresException>(() => AddDepartmentAsync(seeded, "sales"));
        Assert.Equal(UniqueViolation, topLevel.SqlState);

        // A cousin may share it.
        var (engineering, _) = await AddDepartmentAsync(seeded, "Engineering");
        await AddDepartmentAsync(seeded, "Support", engineering);
    }

    [Fact]
    public async Task Another_tenants_department_cannot_be_used_through_the_api_or_around_it()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        var (theirDepartment, theirDepartmentPublicId) = await AddDepartmentAsync(theirs, "Their sales");
        var (myEmployee, myEmployeePublicId) = await AddEmployeeAsync(mine);

        // Through the API: not found in this tenant, so refused before anything is written.
        await using (var db = Core(mine))
        {
            var result = await new UpdateEmployeeFeature.UpdateEmployeeCommandHandler(
                    new EFEmployeeService(db), new EFUserService(db, Tenant(mine.TenantId)),
                    new EFCatalogService(db, Tenant(mine.TenantId)), new Outbox.Outbox(db, TimeProvider.System))
                .Handle(Admin(mine), myEmployeePublicId, new("Ada", "Lovelace", null, theirDepartmentPublicId));
            Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
            Assert.Equal("No such department.", result.Message);
        }

        await using (var db = Core(mine))
        {
            var asParent = await new CreateDepartmentFeature.CreateDepartmentCommandHandler(
                    new EFCatalogService(db, Tenant(mine.TenantId)), new EFEmployeeService(db),
                    new Outbox.Outbox(db, TimeProvider.System))
                .Handle(Admin(mine), new("Mine", theirDepartmentPublicId));
            Assert.Equal(CoreProblems.ValidationFailed, asParent.ProblemCode);
        }

        // Around it: the composite foreign keys refuse what a bug in the resolve step would let through.
        var placement = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(
            $"UPDATE core.employee SET department_id = '{theirDepartment}' WHERE id = '{myEmployee}'"));
        Assert.Equal(ForeignKeyViolation, placement.SqlState);
        var parent = await Assert.ThrowsAsync<PostgresException>(() => AddDepartmentAsync(mine, "Mine", theirDepartment));
        Assert.Equal(ForeignKeyViolation, parent.SqlState);
    }

    [Fact]
    public async Task One_tenant_never_lists_anothers_catalogs()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        await AddDepartmentAsync(theirs, "Their sales");
        await AddDepartmentAsync(mine, "My sales");

        await using var db = Core(mine);
        var result = await new GetDepartmentsFeature.GetDepartmentsQueryHandler(new EFCatalogService(db, Tenant(mine.TenantId)))
            .Handle(Admin(mine));

        var row = Assert.Single(Assert.IsType<List<GetDepartmentsFeature.DepartmentModel>>(result.Value));
        Assert.Equal("My sales", row.Name);
    }

    [Fact]
    public async Task An_app_sees_where_an_employee_sits_and_a_rename_at_once()
    {
        var seeded = await SeedAsync();
        var (_, salesPublicId) = await AddDepartmentAsync(seeded, "Sales");
        var (employee, employeePublicId) = await AddEmployeeAsync(seeded);

        await using (var db = Core(seeded))
        {
            var placed = await new UpdateEmployeeFeature.UpdateEmployeeCommandHandler(
                    new EFEmployeeService(db), new EFUserService(db, Tenant(seeded.TenantId)),
                    new EFCatalogService(db, Tenant(seeded.TenantId)), new Outbox.Outbox(db, TimeProvider.System))
                .Handle(Admin(seeded), employeePublicId, new("Ada", "Lovelace", null, salesPublicId));
            Assert.True(placed.Succeeded, placed.Message);
        }
        Assert.True((await MoveAsync(seeded, salesPublicId, "Sales and marketing", null)).Succeeded);

        await using var tickets = new NpgsqlConnection(fixture.ConnectionStringAs("ap_tickets_rt"));
        await tickets.OpenAsync();
        Assert.Equal("Sales and marketing", await Scalar(tickets,
            $"SELECT department_name FROM core_v1.employee WHERE id = '{employee}'"));
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
