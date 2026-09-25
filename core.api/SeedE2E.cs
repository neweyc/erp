using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Employees;
using AppPlatform.Core.Features.Internal;
using AppPlatform.Core.Services;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Core;

/// <summary>
/// Provisions the tenant the browser journey starts from.
///
/// Runs the REAL provisioning handler rather than inserting rows: hand-written seed SQL is how a
/// fixture drifts from the migrations, which in this repo has already produced a missing column,
/// a missing function, and a total auth failure that all passed their tests.
///
/// It stops at provisioning. The invitation is delivered by the outbox worker and accepted in
/// the browser, because "the invitation arrives" is part of the journey being proved.
///
/// A SECOND tenant is provisioned alongside, for the isolation spec. It exists here rather than
/// being created by an operator mid-run because anonymous sign-in resolves its tenant from
/// configuration (D5), so the core.api that signs tenant B in needs B's id at startup — before
/// any spec runs.
/// </summary>
public static class SeedE2E
{
    public const string TenantName = "E2E Ltd";
    public const string AdminEmail = "admin@e2e.test";
    public const string AdminPassword = "correct horse battery";

    /// <summary>
    /// Someone for a ticket to be assigned to. The journey assigns and closes, and an assignee
    /// picker with an empty roster cannot exercise either.
    /// </summary>
    public const string EmployeeFirstName = "Ada";
    public const string EmployeeLastName = "Lovelace";
    public const string EmployeeEmail = "ada@e2e.test";

    /// <summary>
    /// The second tenant. Deliberately seeded with NO employee: its admin creates one over HTTP
    /// in the isolation spec, so the roster comparison is between two tenants' real writes.
    /// </summary>
    public const string OtherTenantName = "Other Ltd";
    public const string OtherAdminEmail = "admin@other.test";

    /// <summary>
    /// A principal for seed-time writes, required by the handler signature and read by nothing
    /// today — `CreateEmployeeFeature` never looks at it, and neither tenancy, the outbox, nor
    /// SaveChanges references `Caller` at all. So nothing is attributed to this.
    ///
    /// Declared as an API KEY with a null UserId on purpose. The obvious shape —
    /// `Kind = User, UserId = Guid.Empty` — would satisfy `RequireUserId()` and any role check,
    /// which is precisely the guard `Caller` exists to provide: it throws rather than let
    /// `Guid.Empty` be written as though a user with that id had acted. A seed that bypassed that
    /// by construction would start writing zero-guid actors the day audit lands.
    ///
    /// CompanyId is 0 because the handler resolves the company itself; passing 1 would be a
    /// coincidence of single-company tenants rather than a fact.
    /// </summary>
    private static Caller SeedCaller(int tenantId) => new()
    {
        PrincipalId = Guid.Empty,
        Kind = PrincipalKind.ApiKey,
        UserId = null,
        TenantId = tenantId,
        CompanyId = 0,
        Role = "seed",
    };

    public static async Task<int> RunAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        // Each step guards on its OWN existence, not on the tenant's.
        //
        // A single "already seeded?" check on the tenant meant a run that provisioned and then
        // failed to create the employee could never recover: the tenant row survived, every later
        // run reported success, and the journey failed at the assignee picker — which reads as a
        // tickets-UI bug rather than a half-finished seed.
        if (!await ProvisionIfMissingAsync(options, TenantName, AdminEmail, "e2e-seed")) return 1;

        var tenantId = await TenantIdAsync(options);

        if (!await CreateEmployeeIfMissingAsync(options, tenantId)) return 1;

        if (!await ProvisionIfMissingAsync(options, OtherTenantName, OtherAdminEmail, "e2e-seed-other"))
            return 1;

        Console.WriteLine(
            $"seed-e2e: tenants '{TenantName}' and '{OtherTenantName}' ready, unlicensed; employee " +
            $"{EmployeeFirstName} {EmployeeLastName} present in '{TenantName}'; invitations for " +
            $"{AdminEmail} and {OtherAdminEmail} await delivery");

        return 0;
    }

    private static async Task<bool> ProvisionIfMissingAsync(
        DbContextOptions<CoreDbContext> options, string name, string adminEmail, string idempotencyKey)
    {
        var tenant = new AmbientTenantProvider();
        await using var db = new CoreDbContext(options, tenant);

        if (await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Name == name)) return true;

        var provisioned = await new ProvisionTenantFeature.ProvisionTenantCommandHandler(
            db, tenant, TimeProvider.System)
            .Handle(new(name, adminEmail, idempotencyKey));

        if (!provisioned.Succeeded)
        {
            Console.Error.WriteLine($"seed-e2e: provisioning {name} failed: {provisioned.Message}");
            return false;
        }

        // Deliberately does NOT accept the invitation, and does NOT license anything. Acceptance
        // happens in the browser from the DELIVERED message, and licensing is an operator action
        // over HTTP — doing either here would leave the real path unproven.
        return true;
    }

    private static async Task<int> TenantIdAsync(DbContextOptions<CoreDbContext> options)
    {
        // IgnoreQueryFilters: there is no session here to supply a tenant.
        await using var db = new CoreDbContext(options, new AmbientTenantProvider());

        return (await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Name == TenantName)).Id;
    }

    private static async Task<bool> CreateEmployeeIfMissingAsync(
        DbContextOptions<CoreDbContext> options, int tenantId)
    {
        var scoped = new AmbientTenantProvider();
        scoped.UseTenant(tenantId);
        await using var db = new CoreDbContext(options, scoped);

        if (await db.Employees.AnyAsync(e => e.Email == EmployeeEmail)) return true;

        // Through the real handler, as provisioning is. A raw INSERT would skip the validation,
        // the public id, and the domain event every other employee gets.
        var created = await new CreateEmployeeFeature.CreateEmployeeCommandHandler(
            new EFEmployeeService(db), new Outbox.Outbox(db, TimeProvider.System))
            .Handle(SeedCaller(tenantId), new(EmployeeFirstName, EmployeeLastName, EmployeeEmail));

        if (!created.Succeeded)
        {
            Console.Error.WriteLine($"seed-e2e: employee creation failed: {created.Message}");
            return false;
        }

        return true;
    }
}
