using AppPlatform.Api;
using AppPlatform.Audit;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Features.Auth;
using AppPlatform.Core.Features.Employees;
using AppPlatform.Core.Features.Roles;
using AppPlatform.Core.Features.Users;
using AppPlatform.Core.Services;
using AppPlatform.Ids;
using AppPlatform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Roles and account status against real PostgreSQL, as core's runtime role (docs/permissions.md):
/// access changes in a tenant (role writes, deactivation, accepting an invitation) run one at a time
/// under the tenant's role-change lock, a change reaches the session function every service
/// authenticates through, and a custom role never reaches across tenants.
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
        // Every tenant has an address (provisioning writes one); invitations link to it.
        await Scalar(connection,
            "INSERT INTO platform.tenant_subdomain (subdomain, tenant_id, assigned_at) " +
            $"VALUES ('t{Guid.NewGuid():N}'::varchar(20), {tenantId}, now())");
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

    private static EFUserService Users(CoreDbContext db, int tenantId) => new(db, Tenant(tenantId));

    private static AmbientTenantProvider Tenant(int tenantId)
    {
        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(tenantId);
        return tenant;
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

    // ---- Deactivation, reactivation and invitations ----

    private static Task<CommandResult> DeactivateAsync(CoreDbContext db, int tenantId, string userPublicId)
        => new DeactivateUserFeature.DeactivateUserCommandHandler(
                Users(db, tenantId), new EFRoleService(db), new Outbox.Outbox(db, TimeProvider.System), TimeProvider.System)
            .Handle(Admin(tenantId), userPublicId);

    [Fact]
    public async Task Deactivation_waits_for_a_role_change_and_then_sees_it()
    {
        var seeded = await SeedAsync();

        // A role write has demoted the first admin and not committed.
        var (held, heldTransaction) = await HoldLockAsync(seeded.TenantId,
            $"UPDATE identity.\"user\" SET roles = '{{core:member}}' WHERE id = '{seeded.FirstAdmin}'");
        await using var _ = held;

        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var deactivate = DeactivateAsync(db, seeded.TenantId, seeded.SecondAdminPublicId);

        await Task.Delay(BlockedFor);
        Assert.False(deactivate.IsCompleted, "deactivation should be waiting for the role-change lock");

        await heldTransaction.CommitAsync();

        // Counting before the demotion committed, it would have seen two admins and gone ahead.
        Assert.Equal(CoreProblems.LastAdmin, (await deactivate).ProblemCode);
        await using var check = await OpenAsync();
        Assert.Equal("Active", await Scalar(check,
            $"SELECT status FROM identity.\"user\" WHERE id = '{seeded.SecondAdmin}'"));
    }

    [Fact]
    public async Task Deactivation_ends_sessions_at_once_and_reactivation_does_not_revive_them()
    {
        var seeded = await SeedAsync();
        var (member, memberPublicId) = await AddUserAsync(seeded, "{core:member}");
        // A password, so reactivation makes the account active rather than invited again.
        await fixture.ExecuteAsync($"UPDATE identity.\"user\" SET password_hash = 'hash' WHERE id = '{member}'");
        var sessionId = await AddSessionAsync(seeded.TenantId, member);
        Assert.True((await ReadSessionAsTicketsAsync(sessionId)).UserActive);

        await using (var db = Core(seeded.TenantId, seeded.FirstAdmin))
            Assert.True((await DeactivateAsync(db, seeded.TenantId, memberPublicId)).Succeeded);

        var deactivated = await ReadSessionAsTicketsAsync(sessionId);
        Assert.False(deactivated.UserActive);
        Assert.NotNull(deactivated.RevokedAt);

        await using (var db = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var result = await new ReactivateUserFeature.ReactivateUserCommandHandler(
                    Users(db, seeded.TenantId), new EFEmployeeService(db), new Outbox.Outbox(db, TimeProvider.System))
                .Handle(Admin(seeded.TenantId), memberPublicId);
            Assert.True(result.Succeeded, result.Message);
        }

        // The account is back; the old cookie is not. A cookie copied before deactivation stays dead.
        var reactivated = await ReadSessionAsTicketsAsync(sessionId);
        Assert.True(reactivated.UserActive);
        Assert.NotNull(reactivated.RevokedAt);

        await using var check = await OpenAsync();
        Assert.Equal(2L, await Scalar(check,
            "SELECT count(*) FROM core.outbox_event " +
            $"WHERE aggregate_public_id = '{memberPublicId}' AND event_type IN ('user.deactivated', 'user.reactivated')"));
        Assert.Equal(2L, await Scalar(check,
            $"SELECT count(*) FROM core.audit_log WHERE entity_id = '{memberPublicId}' AND actor_id = '{seeded.FirstAdmin}'"));
    }

    [Fact]
    public async Task A_sign_in_waits_for_a_deactivation_in_progress_and_is_then_refused()
    {
        var seeded = await SeedAsync();
        var (member, _) = await AddUserAsync(seeded, "{core:member}");
        string email;
        await using (var connection = await OpenAsync())
            email = (string)(await Scalar(connection, $"SELECT email FROM identity.\"user\" WHERE id = '{member}'"))!;
        await fixture.ExecuteAsync(
            $"UPDATE identity.\"user\" SET password_hash = '{PasswordHasher.Hash("correct horse battery")}' WHERE id = '{member}'");

        // A deactivation holds the lock and has not committed.
        var (held, heldTransaction) = await HoldLockAsync(seeded.TenantId,
            $"UPDATE identity.\"user\" SET status = 'Deactivated' WHERE id = '{member}'");
        await using var _ = held;

        var tenant = new AmbientTenantProvider();
        tenant.UseTenant(seeded.TenantId);
        var actor = new AmbientAuditActor();
        await using var db = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(fixture.ConnectionStringAs("ap_core_rt"))
                .UseSnakeCaseNamingConvention().Options,
            tenant, actor, TimeProvider.System);
        var signIn = new SignInFeature.SignInCommandHandler(
                new EFAuthService(db), new EFUserService(db, tenant), new EFRoleService(db), actor, TimeProvider.System)
            .Handle(seeded.TenantId, new(email, "correct horse battery"));

        await Task.Delay(BlockedFor);
        Assert.False(signIn.IsCompleted, "sign-in should be waiting for the role-change lock");

        await heldTransaction.CommitAsync();

        // Reading the account without the lock, it would have seen Active and created a session the
        // deactivation never revoked, which would work again after reactivation.
        var outcome = await signIn;
        Assert.Null(outcome.SessionId);
        await using var check = await OpenAsync();
        Assert.Equal(0L, await Scalar(check, $"SELECT count(*) FROM identity.session WHERE user_id = '{member}'"));
    }

    [Fact]
    public async Task A_deactivation_waits_for_a_sign_in_in_progress_and_revokes_its_session()
    {
        var seeded = await SeedAsync();
        var (member, memberPublicId) = await AddUserAsync(seeded, "{core:member}");

        // A sign-in holds the lock SHARED and has created its session, not yet committed.
        var signIn = await OpenAsync();
        await using var __ = signIn;
        var signInTransaction = await signIn.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared(hashtext(@key))", signIn))
        {
            lockCommand.Parameters.AddWithValue("key", EFUserService.RoleChangeLockKey(seeded.TenantId));
            await lockCommand.ExecuteScalarAsync();
        }
        var sessionId = Guid.NewGuid();
        await Scalar(signIn,
            "INSERT INTO identity.session (id, tenant_id, user_id, mfa_satisfied, created_at, last_seen_at, absolute_expiry) " +
            $"VALUES ('{sessionId}', {seeded.TenantId}, '{member}', true, now(), now(), now() + interval '1 day')");

        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var deactivate = DeactivateAsync(db, seeded.TenantId, memberPublicId);

        await Task.Delay(BlockedFor);
        Assert.False(deactivate.IsCompleted, "deactivation should wait for the sign-in holding the lock");

        await signInTransaction.CommitAsync();
        Assert.True((await deactivate).Succeeded);

        // The session committed first, so the deactivation found it and revoked it.
        Assert.NotNull((await ReadSessionAsTicketsAsync(sessionId)).RevokedAt);
    }

    [Fact]
    public async Task An_invitation_accepted_while_the_account_is_being_deactivated_is_refused()
    {
        var seeded = await SeedAsync();
        var (invited, _) = await AddUserAsync(seeded, "{core:member}", status: "Invited");
        var (token, plaintext) = InvitationToken.Issue(invited, DateTimeOffset.UtcNow);
        await using (var db = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            db.UserTokens.Add(token);
            await db.SaveChangesAsync();
        }

        // An administrator's deactivation holds the lock and has not committed.
        var (held, heldTransaction) = await HoldLockAsync(seeded.TenantId,
            $"UPDATE identity.\"user\" SET status = 'Deactivated' WHERE id = '{invited}'");
        await using var _ = held;

        var tenant = new AmbientTenantProvider();
        var actor = new AmbientAuditActor();
        await using var acceptDb = new CoreDbContext(
            new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(fixture.ConnectionStringAs("ap_core_rt"))
                .UseSnakeCaseNamingConvention().Options,
            tenant, actor, TimeProvider.System);
        var accept = new AcceptInviteFeature.AcceptInviteCommandHandler(
                new EFAuthService(acceptDb), new EFUserService(acceptDb, tenant), tenant, actor,
                new Core.Services.EFTenantResolver(acceptDb, new Uri("https://app.example.com")), TimeProvider.System)
            .Handle(new(plaintext, "correct horse battery"));

        await Task.Delay(BlockedFor);
        Assert.False(accept.IsCompleted, "acceptance should be waiting for the role-change lock");

        await heldTransaction.CommitAsync();

        // Reading the account before the lock, it would have seen Invited and set it Active over
        // the deactivation.
        Assert.Equal(AuthProblems.InvalidToken, (await accept).ProblemCode);
        await using var check = await OpenAsync();
        Assert.Equal("Deactivated|true", await Scalar(check,
            $"SELECT status || '|' || (password_hash IS NULL) FROM identity.\"user\" WHERE id = '{invited}'"));
    }

    [Fact]
    public async Task The_users_list_reports_the_email_that_carried_the_newest_link()
    {
        var seeded = await SeedAsync();
        var (invited, invitedPublicId) = await AddUserAsync(seeded, "{core:member}", status: "Invited");

        async Task ResendAsync()
        {
            await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
            var result = await new ResendInvitationFeature.ResendInvitationCommandHandler(
                    Users(db, seeded.TenantId), new EFEmployeeService(db), new Outbox.Outbox(db, TimeProvider.System),
                    new Core.Services.EFTenantResolver(db, new Uri("https://app.example.com")), TimeProvider.System)
                .Handle(Admin(seeded.TenantId), invitedPublicId);
            Assert.True(result.Succeeded, result.Message);
        }

        async Task<string> DeliveryAsync()
        {
            await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
            var listed = await new GetUsersFeature.GetUsersQueryHandler(Users(db, seeded.TenantId), new EFEmployeeService(db), TimeProvider.System)
                .Handle(Admin(seeded.TenantId));
            return Assert.IsType<List<GetUsersFeature.UserModel>>(listed.Value)
                .Single(u => u.UserId == invitedPublicId).Invitation!.Delivery;
        }

        await ResendAsync();
        await ResendAsync();
        await using var connection = await OpenAsync();
        var newestMessage = (Guid)(await Scalar(connection,
            $"SELECT message_id FROM identity.user_token WHERE user_id = '{invited}' ORDER BY created_at DESC LIMIT 1"))!;

        // The OLDER email failed, and its row looks newer (interleaved resends commit in either
        // order). Matching by time or address would report "failed"; the newest link's own email
        // is still pending.
        await Scalar(connection,
            "UPDATE core.outbox_message SET status = 'Dead', created_at = now() + interval '1 minute' " +
            $"WHERE id <> '{newestMessage}' AND destination = (SELECT email FROM identity.\"user\" WHERE id = '{invited}')");
        Assert.Equal("pending", await DeliveryAsync());

        // Its email pruned, the newest link's delivery is unknown, not the older failure.
        await Scalar(connection, $"DELETE FROM core.outbox_message WHERE id = '{newestMessage}'");
        Assert.Equal("unknown", await DeliveryAsync());
    }

    // ---- Terminating an employee ends their access, under the same lock ----

    /// <summary>An employee in the seeded tenant, and optionally links an existing account to them.</summary>
    private async Task<(Guid Id, string PublicId)> AddEmployeeAsync(Seeded seeded, Guid? account = null)
    {
        var id = Guid.CreateVersion7();
        var publicId = PublicId.New("emp").ToString();
        await fixture.ExecuteAsync(
            "INSERT INTO core.employee (id, tenant_id, company_id, public_id, first_name, last_name, email, status, version, deleted) " +
            $"VALUES ('{id}', {seeded.TenantId}, {seeded.CompanyId}, '{publicId}', 'Ada', 'Lovelace', '{Guid.NewGuid():N}@roles.test', 'Active', 1, false)");
        if (account is { } user)
            await fixture.ExecuteAsync($"UPDATE identity.\"user\" SET employee_id = '{id}' WHERE id = '{user}'");
        return (id, publicId);
    }

    private static Task<CommandResult> TerminateAsync(CoreDbContext db, int tenantId, string employeePublicId)
        => new TerminateEmployeeFeature.TerminateEmployeeCommandHandler(
                new EFEmployeeService(db), Users(db, tenantId), new EFRoleService(db),
                new Outbox.Outbox(db, TimeProvider.System), TimeProvider.System)
            .Handle(Admin(tenantId), employeePublicId);

    [Fact]
    public async Task Terminating_an_employee_ends_their_sessions_at_once_with_audit_and_events()
    {
        var seeded = await SeedAsync();
        var (member, memberPublicId) = await AddUserAsync(seeded, "{core:member}");
        var (_, employeePublicId) = await AddEmployeeAsync(seeded, member);
        var sessionId = await AddSessionAsync(seeded.TenantId, member);

        await using (var db = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var result = await TerminateAsync(db, seeded.TenantId, employeePublicId);
            Assert.True(result.Succeeded, result.Message);
        }

        // Every service sees it on the next request: the session is revoked, not merely refused.
        var session = await ReadSessionAsTicketsAsync(sessionId);
        Assert.False(session.UserActive);
        Assert.NotNull(session.RevokedAt);

        await using var check = await OpenAsync();
        Assert.Equal("Terminated", await Scalar(check, $"SELECT status FROM core.employee WHERE public_id = '{employeePublicId}'"));
        Assert.Equal(1L, await Scalar(check,
            $"SELECT count(*) FROM core.outbox_event WHERE aggregate_public_id = '{employeePublicId}' AND event_type = 'employee.terminated'"));
        Assert.Equal(1L, await Scalar(check,
            $"SELECT count(*) FROM core.outbox_event WHERE aggregate_public_id = '{memberPublicId}' AND event_type = 'user.deactivated'"));
        // Both changes audited, attributed to the administrator who made them.
        Assert.Equal(2L, await Scalar(check,
            "SELECT count(*) FROM core.audit_log " +
            $"WHERE entity_id IN ('{employeePublicId}', '{memberPublicId}') AND actor_id = '{seeded.FirstAdmin}'"));
    }

    [Fact]
    public async Task Termination_waits_for_a_role_change_and_then_sees_it()
    {
        var seeded = await SeedAsync();
        var (_, employeePublicId) = await AddEmployeeAsync(seeded, seeded.SecondAdmin);

        // A role write has demoted the first admin and not committed.
        var (held, heldTransaction) = await HoldLockAsync(seeded.TenantId,
            $"UPDATE identity.\"user\" SET roles = '{{core:member}}' WHERE id = '{seeded.FirstAdmin}'");
        await using var _ = held;

        await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
        var terminate = TerminateAsync(db, seeded.TenantId, employeePublicId);

        await Task.Delay(BlockedFor);
        Assert.False(terminate.IsCompleted, "termination should be waiting for the role-change lock");

        await heldTransaction.CommitAsync();

        // Counting before the demotion committed, it would have seen two admins and gone ahead.
        Assert.Equal(CoreProblems.LastAdmin, (await terminate).ProblemCode);
        await using var check = await OpenAsync();
        Assert.Equal("Active", await Scalar(check, $"SELECT status FROM core.employee WHERE public_id = '{employeePublicId}'"));
        Assert.Equal("Active", await Scalar(check, $"SELECT status FROM identity.\"user\" WHERE id = '{seeded.SecondAdmin}'"));
    }

    private static Task<CommandResult> EditAsync(CoreDbContext db, int tenantId, string employeePublicId, string firstName)
        => new UpdateEmployeeFeature.UpdateEmployeeCommandHandler(
                new EFEmployeeService(db), Users(db, tenantId), new EFCatalogService(db, Tenant(tenantId)),
                new Outbox.Outbox(db, TimeProvider.System))
            .Handle(Admin(tenantId), employeePublicId, new(firstName, "Lovelace", null));

    private static Task<CommandResult> AddToRosterAsync(CoreDbContext db, int tenantId, string userPublicId)
        => new AddUserToRosterFeature.AddUserToRosterCommandHandler(
                Users(db, tenantId), new EFEmployeeService(db), new Outbox.Outbox(db, TimeProvider.System))
            .Handle(Admin(tenantId), userPublicId, new("Grace", "Hopper"));

    [Fact]
    public async Task An_edit_waits_for_a_termination_in_progress_and_neither_is_lost()
    {
        var seeded = await SeedAsync();
        var (member, memberPublicId) = await AddUserAsync(seeded, "{core:member}");
        var (_, employeePublicId) = await AddEmployeeAsync(seeded, member);

        // Something holding the lock, as a termination does from its first read to its commit.
        var (held, heldTransaction) = await HoldLockAsync(seeded.TenantId, "SELECT 1");
        await using var _ = held;

        await using (var editDb = Core(seeded.TenantId, seeded.FirstAdmin))
        {
            var edit = EditAsync(editDb, seeded.TenantId, employeePublicId, "Augusta");
            await Task.Delay(BlockedFor);
            // Unserialized, an edit and a termination both read version 1 and both emit version 2;
            // the outbox's unique version index then rolls the loser back, deactivation included.
            Assert.False(edit.IsCompleted, "an edit should wait for the role-change lock");
            await heldTransaction.RollbackAsync();
            Assert.True((await edit).Succeeded);
        }

        // And raced for real, both land, each with its own version.
        async Task<CommandResult> Edit()
        {
            await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
            return await EditAsync(db, seeded.TenantId, employeePublicId, "Ada");
        }
        async Task<CommandResult> Terminate()
        {
            await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
            return await TerminateAsync(db, seeded.TenantId, employeePublicId);
        }
        var results = await Task.WhenAll(Edit(), Terminate());
        Assert.All(results, r => Assert.True(r.Succeeded, r.Message));

        await using var check = await OpenAsync();
        Assert.Equal("Terminated", await Scalar(check, $"SELECT status FROM core.employee WHERE public_id = '{employeePublicId}'"));
        Assert.Equal("Deactivated", await Scalar(check, $"SELECT status FROM identity.\"user\" WHERE id = '{member}'"));
        Assert.Equal(3L, await Scalar(check,
            $"SELECT count(DISTINCT aggregate_version) FROM core.outbox_event WHERE aggregate_public_id = '{employeePublicId}'"));
        Assert.Equal(1L, await Scalar(check,
            $"SELECT count(*) FROM core.outbox_event WHERE aggregate_public_id = '{memberPublicId}' AND event_type = 'user.deactivated'"));
    }

    [Fact]
    public async Task Another_tenants_account_cannot_be_put_on_the_roster()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        var (theirAccount, theirAccountPublicId) = await AddUserAsync(theirs, "{core:member}");

        await using (var db = Core(mine.TenantId, mine.FirstAdmin))
            Assert.Equal(CoreProblems.NotFound, (await AddToRosterAsync(db, mine.TenantId, theirAccountPublicId)).ProblemCode);

        await using var check = await OpenAsync();
        Assert.Equal(true, await Scalar(check, $"SELECT employee_id IS NULL FROM identity.\"user\" WHERE id = '{theirAccount}'"));
        Assert.Equal(0L, await Scalar(check,
            $"SELECT count(*) FROM core.employee WHERE tenant_id IN ({mine.TenantId}, {theirs.TenantId}) AND first_name = 'Grace'"));
        Assert.Equal(0L, await Scalar(check,
            $"SELECT count(*) FROM core.outbox_event WHERE aggregate_public_id = '{theirAccountPublicId}'"));
    }

    [Fact]
    public async Task Another_tenants_employee_cannot_be_edited_or_terminated()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        var (_, theirEmployee) = await AddEmployeeAsync(theirs);

        await using (var db = Core(mine.TenantId, mine.FirstAdmin))
        {
            var edit = await EditAsync(db, mine.TenantId, theirEmployee, "Mallory");
            Assert.Equal(CoreProblems.NotFound, edit.ProblemCode);
        }

        await using (var db = Core(mine.TenantId, mine.FirstAdmin))
            Assert.Equal(CoreProblems.NotFound, (await TerminateAsync(db, mine.TenantId, theirEmployee)).ProblemCode);

        await using var check = await OpenAsync();
        Assert.Equal("Ada|Active", await Scalar(check,
            $"SELECT first_name || '|' || status FROM core.employee WHERE public_id = '{theirEmployee}'"));
    }

    [Fact]
    public async Task An_account_put_on_the_roster_twice_at_once_gets_one_employee()
    {
        var seeded = await SeedAsync();
        var (account, accountPublicId) = await AddUserAsync(seeded, "{core:member}");

        async Task<CommandResult> Add()
        {
            await using var db = Core(seeded.TenantId, seeded.FirstAdmin);
            return await AddToRosterAsync(db, seeded.TenantId, accountPublicId);
        }

        var results = await Task.WhenAll(Add(), Add());

        // The second waits for the first's lock, then sees the link it made.
        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => r.ProblemCode == CoreProblems.UserHasEmployee);
        await using var check = await OpenAsync();
        Assert.Equal(1L, await Scalar(check,
            "SELECT count(*) FROM core.employee e JOIN identity.\"user\" u ON u.email = e.email " +
            $"WHERE u.id = '{account}'"));
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
