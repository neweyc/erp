using AppPlatform.Api;
using AppPlatform.Audit;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Roles;
using AppPlatform.Core.Features.Users;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Roles against real PostgreSQL, as core's runtime role (docs/permissions.md): role writes in a
/// tenant run one at a time under the tenant's role-change lock, a change reaches the session
/// function every service authenticates through, and a custom role never reaches across tenants.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class UserRolesTests(PrivilegeFixture fixture)
{
    /// <summary>Long enough that a statement not blocked on a lock would certainly have finished.</summary>
    private static readonly TimeSpan BlockedFor = TimeSpan.FromMilliseconds(500);

    private sealed record Seeded(
        int TenantId, int CompanyId, Guid FirstAdmin, Guid SecondAdmin, string SecondAdminPublicId);

    /// <summary>A fresh tenant, licensed for tickets, with two active administrators.</summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var connection = await OpenAsync();

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
            "INSERT INTO identity.\"user\" (id, tenant_id, company_id, public_id, email, roles, status) VALUES " +
            $"('{first}', {tenantId}, {companyId}, '{PublicId.New("usr")}', 'first@roles.test', '{{core:admin}}', 'Active'), " +
            $"('{second}', {tenantId}, {companyId}, '{secondPublicId}', 'second@roles.test', '{{core:admin}}', 'Active')");

        return new Seeded(tenantId, companyId, first, second, secondPublicId);
    }

    private async Task<(Guid Id, string PublicId)> AddUserAsync(Seeded seeded, string roles, string status = "Active")
    {
        var id = Guid.CreateVersion7();
        var publicId = PublicId.New("usr").ToString();
        await fixture.ExecuteAsync(
            "INSERT INTO identity.\"user\" (id, tenant_id, company_id, public_id, email, roles, status) " +
            $"VALUES ('{id}', {seeded.TenantId}, {seeded.CompanyId}, '{publicId}', '{Guid.NewGuid():N}@roles.test', '{roles}', '{status}')");
        return (id, publicId);
    }

    private async Task<string> AddCustomRoleAsync(Seeded seeded, string name, string permissions)
    {
        var publicId = PublicId.New("rol").ToString();
        await fixture.ExecuteAsync(
            "INSERT INTO identity.role (id, tenant_id, public_id, name, description, permissions, version) " +
            $"VALUES ('{Guid.CreateVersion7()}', {seeded.TenantId}, '{publicId}', '{name}', '', '{permissions}', 1)");
        return publicId;
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

    private static EFUserService Users(CoreDbContext db, int tenantId)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);
        return new EFUserService(db, tenant);
    }

    private static Caller Admin(int tenantId) => new()
    {
        PrincipalId = Guid.NewGuid(), Kind = PrincipalKind.User, UserId = Guid.NewGuid(),
        TenantId = tenantId, CompanyId = 1, Roles = [BuiltInRoles.CoreAdmin], LicensedApps = ["tickets"],
    };

    private static Task<CommandResult> ChangeAsync(
        CoreDbContext db, int tenantId, string userPublicId, string[]? add = null, string[]? remove = null)
        => new ChangeUserRolesFeature.ChangeUserRolesCommandHandler(
                Users(db, tenantId), new EFRoleService(db), new Outbox.Outbox(db, TimeProvider.System))
            .Handle(Admin(tenantId), userPublicId, new(add?.ToList<string?>(), remove?.ToList<string?>()));

    /// <summary>
    /// One side of a race, held open: it holds the tenant's role-change lock, exactly as a role
    /// write does, and has run <paramref name="sql"/>, but has not committed.
    /// </summary>
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldLockAsync(int tenantId, string sql)
    {
        var connection = await OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@key))", connection))
        {
            lockCommand.Parameters.AddWithValue("key", EFUserService.RoleChangeLockKey(tenantId));
            await lockCommand.ExecuteScalarAsync();
        }
        await Scalar(connection, sql);
        return (connection, transaction);
    }

    // ---- Role writes in a tenant run one at a time ----

    [Fact]
    public async Task Two_admins_demoting_each_other_at_once_cannot_leave_the_tenant_with_none()
    {
        var seeded = await SeedAsync();

        // One side has demoted the FIRST admin and not committed.
        var (held, heldTransaction) = await HoldLockAsync(seeded.TenantId,
            $"UPDATE identity.\"user\" SET roles = '{{core:member}}' WHERE id = '{seeded.FirstAdmin}'");
        await using var _ = held;

        // The other, through the real handler, demotes the SECOND.
        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var demoteSecond = ChangeAsync(db, seeded.TenantId, seeded.SecondAdminPublicId, remove: [BuiltInRoles.CoreAdmin]);

        await Task.Delay(BlockedFor);
        Assert.False(demoteSecond.IsCompleted, "the handler should be waiting for the role-change lock");

        await heldTransaction.CommitAsync();

        // It read the users after the first demotion committed, so it sees the second admin is
        // the last. Reading first, it would have counted both and succeeded.
        Assert.Equal(CoreProblems.LastAdmin, (await demoteSecond).ProblemCode);
        await using var check = await OpenAsync();
        Assert.Equal(1L, await Scalar(check,
            $"SELECT count(*) FROM identity.\"user\" WHERE tenant_id = {seeded.TenantId} AND 'core:admin' = ANY (roles)"));
    }

    [Fact]
    public async Task A_second_change_to_the_same_user_waits_and_works_from_the_first()
    {
        var seeded = await SeedAsync();
        var (member, memberPublicId) = await AddUserAsync(seeded, "{core:member}");

        var (held, heldTransaction) = await HoldLockAsync(seeded.TenantId,
            $"UPDATE identity.\"user\" SET roles = '{{core:member,tickets:requester}}', access_version = 2 WHERE id = '{member}'");
        await using var _ = held;

        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var change = ChangeAsync(db, seeded.TenantId, memberPublicId, add: ["core:manager"]);

        await Task.Delay(BlockedFor);
        Assert.False(change.IsCompleted, "the handler should be waiting for the role-change lock");

        await heldTransaction.CommitAsync();
        var result = await change;
        Assert.True(result.Succeeded, result.Message);

        // Both changes survive, and the second numbered itself after the first. Reading first
        // would have written roles without tickets:requester, and version 2 a second time.
        await using var check = await OpenAsync();
        Assert.Equal("{core:manager,core:member,tickets:requester}|3", await Scalar(check,
            $"SELECT roles::text || '|' || access_version FROM identity.\"user\" WHERE id = '{member}'"));
    }

    [Fact]
    public async Task A_role_cannot_be_deleted_while_an_assignment_of_it_is_in_flight()
    {
        var seeded = await SeedAsync();
        var clerk = await AddCustomRoleAsync(seeded, "Clerk", "{tickets.ticket.read}");
        var (member, _) = await AddUserAsync(seeded, "{core:member}");

        // One side has given the member the role and not committed.
        var (held, heldTransaction) = await HoldLockAsync(seeded.TenantId,
            $"UPDATE identity.\"user\" SET roles = '{{core:member,{clerk}}}' WHERE id = '{member}'");
        await using var _ = held;

        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var delete = new DeleteRoleFeature.DeleteRoleCommandHandler(
                Users(db, seeded.TenantId), new EFRoleService(db), new Outbox.Outbox(db, TimeProvider.System))
            .Handle(Admin(seeded.TenantId), clerk);

        await Task.Delay(BlockedFor);
        Assert.False(delete.IsCompleted, "the delete should be waiting for the role-change lock");

        await heldTransaction.CommitAsync();

        // Without the lock it would have counted no holders and deleted a role someone now holds.
        Assert.Equal(CoreProblems.RoleInUse, (await delete).ProblemCode);
    }

    [Fact]
    public async Task A_role_change_does_not_break_another_write_to_the_same_user()
    {
        // A user is loaded BEFORE an administrator changes their roles and saved AFTER, as accepting
        // an invitation or rehashing a password does. Shown with a password rehash. A concurrency
        // token on the user would make that save match no row and fail (Cycle 12's review).
        var seeded = await SeedAsync();

        await using var accepting = Core(seeded.TenantId, seeded.SecondAdmin);
        var loaded = await accepting.Users.FirstAsync(u => u.Id == seeded.SecondAdmin);

        await using (var admin = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var result = await ChangeAsync(admin, seeded.TenantId, seeded.SecondAdminPublicId, add: ["tickets:agent"]);
            Assert.True(result.Succeeded, result.Message);
        }

        loaded.PasswordHash = "rehashed";
        await accepting.SaveChangesAsync();

        await using var check = await OpenAsync();
        Assert.Equal("rehashed|{core:admin,tickets:agent}", await Scalar(check,
            $"SELECT password_hash || '|' || roles::text FROM identity.\"user\" WHERE id = '{seeded.SecondAdmin}'"));
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task Another_tenants_user_cannot_be_changed()
    {
        var victim = await SeedAsync();
        var attacker = await SeedAsync();

        await using var db = Core(attacker.TenantId, attacker.FirstAdmin);
        var result = await ChangeAsync(db, attacker.TenantId, victim.SecondAdminPublicId,
            add: ["tickets:agent"], remove: [BuiltInRoles.CoreAdmin]);

        Assert.Equal(CoreProblems.NotFound, result.ProblemCode);

        await using var check = await OpenAsync();
        Assert.Equal("{core:admin}|1", await Scalar(check,
            $"SELECT roles::text || '|' || access_version FROM identity.\"user\" WHERE id = '{victim.SecondAdmin}'"));
        Assert.Equal(0L, await Scalar(check,
            $"SELECT count(*) FROM core.outbox_event WHERE aggregate_public_id = '{victim.SecondAdminPublicId}'"));
    }

    [Fact]
    public async Task Another_tenants_custom_role_cannot_be_given()
    {
        var owner = await SeedAsync();
        var foreignRole = await AddCustomRoleAsync(owner, "Owners clerk", "{core.user.manage}");
        var attacker = await SeedAsync();

        await using var db = Core(attacker.TenantId, attacker.FirstAdmin);
        var result = await ChangeAsync(db, attacker.TenantId, attacker.SecondAdminPublicId, add: [foreignRole]);

        Assert.Equal(CoreProblems.ValidationFailed, result.ProblemCode);
    }

    [Fact]
    public async Task A_session_never_gains_permissions_from_another_tenants_role()
    {
        // The write path refuses another tenant's role id (above). This is the read side: even if
        // one were stored by hand, session_context joins custom roles on the user's OWN tenant.
        var owner = await SeedAsync();
        var foreignRole = await AddCustomRoleAsync(owner, "Foreign", "{tickets.ticket.close}");
        var victim = await SeedAsync();
        var (user, _) = await AddUserAsync(victim, $"{{core:member,{foreignRole}}}");
        var sessionId = await AddSessionAsync(victim.TenantId, user);

        var context = await ReadSessionAsTicketsAsync(sessionId);

        Assert.Empty(context.RolePermissions);
    }

    // ---- Reaching every service ----

    [Fact]
    public async Task Roles_built_in_and_custom_reach_the_session_every_service_authenticates_through()
    {
        var seeded = await SeedAsync();
        var closer = await AddCustomRoleAsync(seeded, "Closer", "{tickets.ticket.read,tickets.ticket.close}");
        var sessionId = await AddSessionAsync(seeded.TenantId, seeded.SecondAdmin);

        await using (var db = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var result = await ChangeAsync(db, seeded.TenantId, seeded.SecondAdminPublicId,
                add: ["tickets:requester", closer]);
            Assert.True(result.Succeeded, result.Message);
        }

        // Resolved as the TICKETS runtime role, the way tickets.api authenticates its own callers.
        var context = await ReadSessionAsTicketsAsync(sessionId);

        Assert.Equal([BuiltInRoles.CoreAdmin, closer, "tickets:requester"], context.Roles);
        Assert.Equal(["tickets.ticket.close", "tickets.ticket.read"], context.RolePermissions);

        // A cookie issued before the change no longer matches; the live permissions already apply.
        var issued = Access.Stamp(Access.Granted([BuiltInRoles.CoreAdmin], []));
        Assert.Equal(AuthProblem.RoleChanged, SessionEvaluator.Evaluate(context, issued, DateTimeOffset.UtcNow).ProblemCode);
        var caller = SessionEvaluator.Evaluate(context, ticketAccess: null, DateTimeOffset.UtcNow).Caller!;
        Assert.Contains(Permissions.Tickets.TicketClose, caller.Permissions);
        Assert.Contains(Permissions.Tickets.TicketCreate, caller.Permissions);
    }

    [Fact]
    public async Task Editing_a_custom_role_changes_its_holders_permissions_at_once()
    {
        var seeded = await SeedAsync();
        var clerk = await AddCustomRoleAsync(seeded, "Clerk", "{tickets.ticket.read}");
        var (user, _) = await AddUserAsync(seeded, $"{{core:member,{clerk}}}");
        var sessionId = await AddSessionAsync(seeded.TenantId, user);

        await using (var db = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var result = await new UpdateRoleFeature.UpdateRoleCommandHandler(
                    Users(db, seeded.TenantId), new EFRoleService(db), new Outbox.Outbox(db, TimeProvider.System))
                .Handle(Admin(seeded.TenantId), clerk,
                    new RoleRules.RoleDefinitionCommand("Clerk", "", [Permissions.Tickets.TicketRead, Permissions.Tickets.TicketClose]));
            Assert.True(result.Succeeded, result.Message);
        }

        var caller = SessionEvaluator.Evaluate(await ReadSessionAsTicketsAsync(sessionId), null, DateTimeOffset.UtcNow).Caller!;
        Assert.Contains(Permissions.Tickets.TicketClose, caller.Permissions);
    }

    // ---- Audit, events, the database's own guarantees ----

    [Fact]
    public async Task A_role_change_is_audited_with_its_actor_and_emits_an_event()
    {
        var seeded = await SeedAsync();

        await using (var db = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var result = await ChangeAsync(db, seeded.TenantId, seeded.SecondAdminPublicId,
                add: ["core:manager", "tickets:requester"], remove: [BuiltInRoles.CoreAdmin]);
            Assert.True(result.Succeeded, result.Message);
        }

        await using var connection = await OpenAsync();

        var changes = (string)(await Scalar(connection,
            "SELECT changes::text FROM core.audit_log " +
            $"WHERE entity_id = '{seeded.SecondAdminPublicId}' AND actor_id = '{seeded.FirstAdmin}'"))!;
        Assert.Contains("\"roles\"", changes, StringComparison.Ordinal);
        Assert.Contains("tickets:requester", changes, StringComparison.Ordinal);

        Assert.Equal(1L, await Scalar(connection,
            "SELECT count(*) FROM core.outbox_event " +
            $"WHERE aggregate_public_id = '{seeded.SecondAdminPublicId}' AND event_type = 'user.roles_changed' " +
            "AND aggregate_version = 2"));
    }

    [Fact]
    public async Task Role_names_are_unique_per_tenant_ignoring_case_in_the_database_itself()
    {
        // The backstop for anything that writes a role without the handler's check.
        var seeded = await SeedAsync();
        await AddCustomRoleAsync(seeded, "Clerk", "{core.employee.read}");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => AddCustomRoleAsync(seeded, "CLERK", "{core.employee.read}"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        // Another tenant may use the same name.
        await AddCustomRoleAsync(await SeedAsync(), "Clerk", "{core.employee.read}");
    }

    private async Task<Guid> AddSessionAsync(int tenantId, Guid userId)
    {
        var sessionId = Guid.NewGuid();
        await fixture.ExecuteAsync(
            "INSERT INTO identity.session (id, tenant_id, user_id, mfa_satisfied, created_at, last_seen_at, absolute_expiry) " +
            $"VALUES ('{sessionId}', {tenantId}, '{userId}', true, now(), now(), now() + interval '1 day')");
        return sessionId;
    }

    private async Task<SessionContext> ReadSessionAsTicketsAsync(Guid sessionId)
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionStringAs("ap_tickets_rt"));
        return (await new NpgsqlSessionStore(dataSource).FindAsync(sessionId))!;
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
