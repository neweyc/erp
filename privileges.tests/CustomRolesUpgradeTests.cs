using AppPlatform.Auth;
using AppPlatform.Boundary;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Upgrading a database that already holds Cycle 12 users (one core role in <c>role</c>, app roles in
/// <c>app_roles</c>) through Cycle 13's migrations, using the GENERATED scripts Chris applies.
///
/// The shared fixture cannot catch a broken backfill: it seeds after every migration has run, so
/// there is nothing to backfill. The first review of Cycle 13 found exactly that gap: the scripts
/// still wrote <c>core.admin</c> after the keys became <c>core:admin</c>, which would have left every
/// upgraded administrator with no permissions and every test green.
/// </summary>
public class CustomRolesUpgradeTests : IAsyncLifetime
{
    /// <summary>The first migration of Cycle 13; everything before it is the Cycle 12 schema.</summary>
    private const string FirstCycle13Migration = "20261016194441_AddCustomRoles";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("appplatform")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Cycle_12_users_keep_their_access_through_the_upgrade()
    {
        var privileges = RepositoryPaths.Project("database/privileges");
        await ExecuteFileAsync(Path.Combine(privileges, "00-roles.sql"));
        await ExecuteFileAsync(Path.Combine(privileges, "01-schemas.sql"));
        await ExecuteFileAsync(Path.Combine(RepositoryPaths.Project("database/platform"), "migrations-all.sql"), "ap_owner");

        // Core as it stood at the end of Cycle 12: the idempotent script up to the first block of
        // Cycle 13's first migration, committed.
        var coreScript = await File.ReadAllTextAsync(
            Path.Combine(RepositoryPaths.Project("database/core"), "migrations-all.sql"));
        var cut = coreScript.LastIndexOf("DO $EF$", coreScript.IndexOf(FirstCycle13Migration, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(cut > 0, "the script should contain Cycle 13's first migration");
        await ExecuteAsync($"SET ROLE ap_owner;\n{coreScript[..cut]}\nCOMMIT;");

        // Cycle 12 users, written in Cycle 12's shape.
        await ExecuteAsync("""
            INSERT INTO platform.tenant (id, public_id, name, status, created_at)
              VALUES (1, 'ten_upgrade000000000000000000', 'Upgrade Ltd', 'Active', now());
            INSERT INTO platform.tenant_app (tenant_id, app, granted_at) VALUES (1, 'tickets', now());
            INSERT INTO core.company (id, tenant_id, public_id, name, active)
              VALUES (1, 1, 'co_upgrade0000000000000000000', 'Upgrade Ltd', true);
            INSERT INTO identity."user" (id, tenant_id, company_id, public_id, email, role, app_roles, status) VALUES
              ('00000000-0000-0000-0000-00000000000a', 1, 1, 'usr_admin', 'admin@up.test', 'admin', '{tickets:agent}', 'Active'),
              ('00000000-0000-0000-0000-00000000000b', 1, 1, 'usr_manager', 'manager@up.test', 'manager', '{}', 'Active'),
              ('00000000-0000-0000-0000-00000000000c', 1, 1, 'usr_member', 'member@up.test', 'member', '{tickets:requester,bogus,ledger:,NULL,"",:agent,core:}', 'Active'),
              -- Entries Cycle 12 ignored: a core role in app_roles, and a second entry for one app.
              ('00000000-0000-0000-0000-00000000000d', 1, 1, 'usr_odd', 'odd@up.test', 'member', '{core:admin,tickets:requester,tickets:agent}', 'Active'),
              -- The first ledger entry is an unknown role (its name has a colon); it wins and grants nothing.
              ('00000000-0000-0000-0000-00000000000e', 1, 1, 'usr_odder', 'odder@up.test', 'member', '{ledger:unknown:role,ledger:controller}', 'Active');
            INSERT INTO identity.session (id, tenant_id, user_id, mfa_satisfied, created_at, last_seen_at, absolute_expiry)
              VALUES ('00000000-0000-0000-0000-0000000000aa', 1, '00000000-0000-0000-0000-00000000000a', true, now(), now(), now() + interval '1 day');
            """);

        // The upgrade: the whole script again. It is idempotent, so only Cycle 13 runs now.
        await ExecuteAsync($"SET ROLE ap_owner;\n{coreScript}");
        await ExecuteFileAsync(Path.Combine(privileges, "02-grants.sql"));

        Assert.Equal(
            [
                "admin@up.test {core:admin,tickets:agent}",
                "manager@up.test {core:manager}",
                // Malformed Cycle 12 entries granted nothing then and are not carried over.
                "member@up.test {core:member,tickets:requester}",
                // Cycle 12 ignored core:admin here and used only the first tickets entry; so does the
                // upgrade. Copying them would have made this member an administrator.
                "odd@up.test {core:member,tickets:requester}",
                "odder@up.test {core:member,ledger:unknown:role}",
            ],
            await QueryAsync("SELECT email || ' ' || roles::text FROM identity.\"user\" ORDER BY email"));

        // And what they mean: the administrator still manages users and still works tickets.
        await ExecuteAsync("ALTER ROLE ap_tickets_rt PASSWORD 'test';");
        await using var dataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Username = "ap_tickets_rt", Password = "test" }.ConnectionString);
        var context = await new NpgsqlSessionStore(dataSource).FindAsync(Guid.Parse("00000000-0000-0000-0000-0000000000aa"));
        var caller = SessionEvaluator.Evaluate(context, ticketAccess: null, DateTimeOffset.UtcNow).Caller!;

        Assert.Contains(Permissions.Core.UserManage, caller.Permissions);
        Assert.Contains(Permissions.Tickets.TicketClose, caller.Permissions);
    }

    private async Task ExecuteFileAsync(string path, string? asRole = null)
    {
        // As the shared fixture does: psql meta-commands are stripped, the rest runs as given.
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(path)).Where(line => !line.TrimStart().StartsWith('\\')));
        await ExecuteAsync(asRole is null ? sql : $"SET ROLE {asRole};\n{sql}");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }
}
