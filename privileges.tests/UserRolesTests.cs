using AppPlatform.Api;
using AppPlatform.Audit;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Users;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Outbox;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Changing roles against real PostgreSQL, as core's runtime role: the change reaches the session
/// function every service authenticates through, and the last-administrator rule holds under a race.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class UserRolesTests(PrivilegeFixture fixture)
{
    /// <summary>Long enough that a statement not blocked on a lock would certainly have finished.</summary>
    private static readonly TimeSpan BlockedFor = TimeSpan.FromMilliseconds(500);

    private sealed record Seeded(int TenantId, Guid FirstAdmin, Guid SecondAdmin, string SecondAdminPublicId);

    /// <summary>A fresh tenant, licensed for tickets, with two active administrators.</summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var tenantId = (int)(await Scalar(connection,
            "INSERT INTO platform.tenant (public_id, name, status, created_at) " +
            $"VALUES ('{PublicId.New("ten")}', 'Roles {Guid.NewGuid():N}', 'Active', now()) RETURNING id"))!;
        await Scalar(connection,
            $"INSERT INTO platform.tenant_app (tenant_id, app, granted_at) VALUES ({tenantId}, 'tickets', now())");
        var companyId = (int)(await Scalar(connection,
            "INSERT INTO core.company (tenant_id, public_id, name, active) " +
            $"VALUES ({tenantId}, '{PublicId.New("co")}', 'Roles Ltd', true) RETURNING id"))!;

        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var secondPublicId = PublicId.New("usr").ToString();
        await Scalar(connection,
            "INSERT INTO identity.\"user\" (id, tenant_id, company_id, public_id, email, role, status) VALUES " +
            $"('{first}', {tenantId}, {companyId}, '{PublicId.New("usr")}', 'first@roles.test', 'admin', 'Active'), " +
            $"('{second}', {tenantId}, {companyId}, '{secondPublicId}', 'second@roles.test', 'admin', 'Active')");

        return new Seeded(tenantId, first, second, secondPublicId);
    }

    private CoreDbContext Core(int tenantId, Guid actingUser)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);

        return new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(fixture.ConnectionStringAs("ap_core_rt"))
                .UseSnakeCaseNamingConvention().Options,
            tenant, new AmbientAuditActor(AuditActor.User(actingUser)), TimeProvider.System);
    }

    private static Task<CommandResult> ChangeAsync(
        CoreDbContext db, int tenantId, string userPublicId, ChangeUserRolesFeature.ChangeUserRolesCommand cmd)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);
        var caller = new Caller
        {
            PrincipalId = Guid.NewGuid(), Kind = PrincipalKind.User, UserId = Guid.NewGuid(),
            TenantId = tenantId, CompanyId = 1, Role = "admin", LicensedApps = ["tickets"],
        };

        return new ChangeUserRolesFeature.ChangeUserRolesCommandHandler(new EFUserService(db, tenant), new Outbox.Outbox(db, TimeProvider.System))
            .Handle(caller, userPublicId, cmd);
    }

    [Fact]
    public async Task Two_admins_demoting_each_other_at_once_cannot_leave_the_tenant_with_none()
    {
        var seeded = await SeedAsync();

        // One side, held open: it has locked the administrators and demoted the FIRST admin, but
        // not yet committed. It does not take the role-change lock, so this proves the row lock on
        // its own: it is what stops any other write to an administrator racing the check.
        await using var held = new NpgsqlConnection(fixture.ConnectionString);
        await held.OpenAsync();
        await using var heldTransaction = await held.BeginTransactionAsync();
        await Scalar(held,
            $"SELECT id FROM identity.\"user\" WHERE tenant_id = {seeded.TenantId} AND role = 'admin' " +
            "AND status = 'Active' ORDER BY id FOR UPDATE");
        await Scalar(held, $"UPDATE identity.\"user\" SET role = 'member' WHERE id = '{seeded.FirstAdmin}'");

        // The other side, through the real handler: demote the SECOND admin.
        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var demoteSecond = ChangeAsync(db, seeded.TenantId, seeded.SecondAdminPublicId, new("member", null));

        await Task.Delay(BlockedFor);
        Assert.False(demoteSecond.IsCompleted, "the handler should be waiting on the administrator lock");

        await heldTransaction.CommitAsync();

        // Once the first demotion commits, the handler's locked set holds only the second admin,
        // so demoting them would leave none. Without FOR UPDATE it would have read both as admins
        // (the first change was uncommitted) and succeeded.
        var result = await demoteSecond;
        Assert.Equal(CoreProblems.LastAdmin, result.ProblemCode);

        await using var check = new NpgsqlConnection(fixture.ConnectionString);
        await check.OpenAsync();
        Assert.Equal(1L, await Scalar(check,
            $"SELECT count(*) FROM identity.\"user\" WHERE tenant_id = {seeded.TenantId} AND role = 'admin'"));
    }

    [Fact]
    public async Task A_second_change_to_the_same_user_waits_and_works_from_the_first()
    {
        var seeded = await SeedAsync();

        // A member, not an administrator, so no administrator row lock can be what makes the
        // second change wait: only the tenant's role-change lock covers this user.
        var member = Guid.CreateVersion7();
        var memberPublicId = PublicId.New("usr").ToString();
        await fixture.ExecuteAsync(
            "INSERT INTO identity.\"user\" (id, tenant_id, company_id, public_id, email, role, status) " +
            $"SELECT '{member}', tenant_id, company_id, '{memberPublicId}', 'member@roles.test', 'member', 'Active' " +
            $"FROM identity.\"user\" WHERE id = '{seeded.FirstAdmin}'");

        // One side, held open: it holds the role-change lock, as the handler does, and has given the
        // member a tickets role, but not yet committed.
        await using var held = new NpgsqlConnection(fixture.ConnectionString);
        await held.OpenAsync();
        await using var heldTransaction = await held.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@key))", held))
        {
            lockCommand.Parameters.AddWithValue("key", EFUserService.RoleChangeLockKey(seeded.TenantId));
            await lockCommand.ExecuteScalarAsync();
        }
        await Scalar(held,
            "UPDATE identity.\"user\" SET app_roles = '{tickets:requester}', access_version = 2 " +
            $"WHERE id = '{member}'");

        // The other side, through the real handler: change the same user's core role.
        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var change = ChangeAsync(db, seeded.TenantId, memberPublicId, new("manager", null));

        await Task.Delay(BlockedFor);
        Assert.False(change.IsCompleted, "the handler should be waiting for the role-change lock");

        await heldTransaction.CommitAsync();
        var result = await change;
        Assert.True(result.Succeeded, result.Message);

        // The handler read the user after the first change committed: it kept that change and
        // numbered its own after it. Reading first would have written version 2 a second time.
        await using var check = new NpgsqlConnection(fixture.ConnectionString);
        await check.OpenAsync();
        Assert.Equal("manager|{tickets:requester}|3", await Scalar(check,
            "SELECT role || '|' || app_roles::text || '|' || access_version FROM identity.\"user\" " +
            $"WHERE id = '{member}'"));
    }

    [Fact]
    public async Task A_role_change_does_not_break_another_write_to_the_same_user()
    {
        // The interleaving the review found: a user is loaded BEFORE an administrator changes their
        // roles and saved AFTER, as accepting an invitation or rehashing a password does. Shown here
        // with a password rehash. A concurrency token on the user would make that save match no
        // row and fail.
        var seeded = await SeedAsync();

        await using var accepting = Core(seeded.TenantId, seeded.SecondAdmin);
        var loaded = await accepting.Users.FirstAsync(u => u.Id == seeded.SecondAdmin);

        await using (var admin = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var result = await ChangeAsync(admin, seeded.TenantId, seeded.SecondAdminPublicId,
                new(null, new() { ["tickets"] = "agent" }));
            Assert.True(result.Succeeded, result.Message);
        }

        loaded.PasswordHash = "rehashed";
        await accepting.SaveChangesAsync();

        // Each write kept its own columns.
        await using var check = new NpgsqlConnection(fixture.ConnectionString);
        await check.OpenAsync();
        Assert.Equal("rehashed|{tickets:agent}", await Scalar(check,
            $"SELECT password_hash || '|' || app_roles::text FROM identity.\"user\" WHERE id = '{seeded.SecondAdmin}'"));
    }

    [Fact]
    public async Task Another_tenants_user_cannot_be_changed()
    {
        var victim = await SeedAsync();
        var attacker = await SeedAsync();

        await using var db = Core(attacker.TenantId, attacker.FirstAdmin);
        var result = await ChangeAsync(db, attacker.TenantId, victim.SecondAdminPublicId,
            new("member", new() { ["tickets"] = "agent" }));

        Assert.Equal(CoreProblems.NotFound, result.ProblemCode);

        await using var check = new NpgsqlConnection(fixture.ConnectionString);
        await check.OpenAsync();
        Assert.Equal("admin|{}|1", await Scalar(check,
            "SELECT role || '|' || app_roles::text || '|' || access_version FROM identity.\"user\" " +
            $"WHERE id = '{victim.SecondAdmin}'"));
        Assert.Equal(0L, await Scalar(check,
            $"SELECT count(*) FROM core.outbox_event WHERE aggregate_public_id = '{victim.SecondAdminPublicId}'"));
    }

    [Fact]
    public async Task A_role_change_reaches_the_session_every_service_authenticates_through()
    {
        var seeded = await SeedAsync();
        var sessionId = Guid.NewGuid();
        await fixture.ExecuteAsync(
            "INSERT INTO identity.session (id, tenant_id, user_id, mfa_satisfied, created_at, last_seen_at, absolute_expiry) " +
            $"VALUES ('{sessionId}', {seeded.TenantId}, '{seeded.SecondAdmin}', true, now(), now(), now() + interval '1 day')");

        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var result = await ChangeAsync(db, seeded.TenantId, seeded.SecondAdminPublicId,
            new(null, new() { ["tickets"] = "agent" }));
        Assert.True(result.Succeeded, result.Message);

        // Resolved as the TICKETS runtime role, the way tickets.api authenticates its own callers.
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionStringAs("ap_tickets_rt"));
        var context = await new NpgsqlSessionStore(dataSource).FindAsync(sessionId);

        Assert.Equal(new Dictionary<string, string> { ["tickets"] = "agent" }, context!.AppRoles);

        // A cookie issued before the change no longer matches; the live permissions already apply.
        Assert.Equal(AuthProblem.RoleChanged,
            SessionEvaluator.Evaluate(context, "admin", DateTimeOffset.UtcNow).ProblemCode);
        var caller = SessionEvaluator.Evaluate(context, ticketAccess: null, DateTimeOffset.UtcNow).Caller!;
        Assert.Contains(Permissions.Tickets.TicketClose, caller.Permissions);
    }

    [Fact]
    public async Task A_role_change_is_audited_with_its_actor_and_emits_an_event()
    {
        var seeded = await SeedAsync();

        await using (var db = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var result = await ChangeAsync(db, seeded.TenantId, seeded.SecondAdminPublicId,
                new("manager", new() { ["tickets"] = "requester" }));
            Assert.True(result.Succeeded, result.Message);
        }

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var changes = (string)(await Scalar(connection,
            "SELECT changes::text FROM core.audit_log " +
            $"WHERE entity_id = '{seeded.SecondAdminPublicId}' AND actor_id = '{seeded.FirstAdmin}'"))!;
        Assert.Contains("\"role\"", changes, StringComparison.Ordinal);
        Assert.Contains("tickets:requester", changes, StringComparison.Ordinal);

        Assert.Equal(1L, await Scalar(connection,
            "SELECT count(*) FROM core.outbox_event " +
            $"WHERE aggregate_public_id = '{seeded.SecondAdminPublicId}' AND event_type = 'user.roles_changed' " +
            "AND aggregate_version = 2"));
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
