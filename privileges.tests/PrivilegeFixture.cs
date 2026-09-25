using AppPlatform.Boundary;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// A throwaway PostgreSQL built from the SHIPPED scripts, in the order the runbook specifies:
/// roles and default privileges, then each service's generated migrations, then grants.
///
/// The ordering is part of what is under test. Default privileges apply only to objects created
/// after they are set, so running 01 after the migrations would leave every table ungranted — a
/// mistake this suite should catch rather than replicate.
///
/// It used to apply a hand-written `Fixtures/test-migrations.sql` instead. That drifted from the
/// real schema three separate times — a missing `created_at`, a lowercase tenant status that
/// masked a total auth failure, and a `platform.tenant` with no `created_at` again — and each time
/// the suite passed while the application was broken. A fixture that diverges from the migrations
/// does not merely miss bugs; it certifies them. The scripts are the only acceptable source.
/// </summary>
public sealed class PrivilegeFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("appplatform")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// A connection string authenticating as an application runtime role rather than the
    /// superuser, so a query runs under the grants the scripts actually issue.
    /// </summary>
    public string ConnectionStringAs(string role)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = role,
            Password = "test",
        };

        return builder.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var privileges = RepositoryPaths.Project("database/privileges");

        await ExecuteFileAsync(Path.Combine(privileges, "01-roles-and-schemas.sql"));

        // Applied AS ap_owner, not as the superuser.
        //
        // Ownership is not cosmetic here. A view and a SECURITY DEFINER function execute with
        // their OWNER's privileges, so if migrations run as `postgres` the published view is
        // owned by a role the grant model knows nothing about: `identity_v1.touch_session` then
        // cannot UPDATE `identity.session`, and 99-verify correctly reports the views as
        // misowned. Both happened the moment this fixture switched to the real scripts.
        //
        // ap_owner is a member of every migration role — set up for exactly this reason — so it
        // can create in `core`/`identity`/`tickets`/`platform` as well as in the published
        // schemas, which no single migration role can do.
        foreach (var service in new[] { "platform", "core", "tickets" })
        {
            await ExecuteFileAsync(
                Path.Combine(RepositoryPaths.Project($"database/{service}"), "migrations-all.sql"),
                asRole: "ap_owner");
        }

        await ExecuteFileAsync(Path.Combine(privileges, "02-grants.sql"));
        await ExecuteAsync(SeedSql);

        // Test-only passwords. The scripts deliberately create these roles without one, so
        // they cannot authenticate in a real deployment until a password is issued out of
        // band — but connecting AS the runtime role is the only way to test that the grants
        // actually constrain it.
        await ExecuteAsync("ALTER ROLE ap_core_rt PASSWORD 'test'; ALTER ROLE ap_tickets_rt PASSWORD 'test'; ALTER ROLE ap_platform_rt PASSWORD 'test';");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Strips psql meta-commands, which Npgsql cannot execute. Only backslash directives
    /// are removed; if a script ever needs \i or \copy to be meaningful, it has outgrown
    /// being run this way and the test should fail rather than silently skip it.
    /// </summary>
    private async Task ExecuteFileAsync(string path, string? asRole = null)
    {
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(path))
            .Where(line => !line.TrimStart().StartsWith('\\')));

        // SET ROLE has to be in the same batch: each ExecuteAsync opens its own connection, and a
        // role set on a previous one would not carry over.
        await ExecuteAsync(asRole is null ? sql : $"SET ROLE {asRole};\n{sql}");
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs <paramref name="sql"/> as <paramref name="role"/>; null means no error.</summary>
    public async Task<string?> TryAsAsync(string role, string sql)
    {
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SET ROLE {role}; {sql}", connection);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    public async Task<int> VerificationFindingsAsync()
    {
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(
                Path.Combine(RepositoryPaths.Project("database/privileges"), "99-verify.sql")))
            .Where(line => !line.TrimStart().StartsWith('\\')));

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var count = 0;
        while (await reader.ReadAsync()) count++;
        return count;
    }

    /// <summary>
    /// Seed rows, with every column NAMED.
    ///
    /// Positional INSERTs were what let the old hand-written schema drift unnoticed: adding a
    /// column shifted the values silently. Naming them means a schema change is a compile-time
    /// style failure here rather than a value landing in the wrong field.
    /// </summary>
    private const string SeedSql = """
        INSERT INTO platform.tenant (public_id, name, status, created_at)
          VALUES ('ten_a', 'Acme', 'Active', now());

        INSERT INTO platform.tenant_app (tenant_id, app, granted_at)
          VALUES (1, 'tickets', now());

        INSERT INTO core.company (tenant_id, public_id, name, active)
          VALUES (1, 'co_a', 'Acme Ltd', true);

        INSERT INTO core.employee
          (id, tenant_id, company_id, public_id, first_name, last_name, email, status, version, deleted)
          VALUES ('11111111-1111-1111-1111-111111111111', 1, 1, 'emp_x',
                  'Ada', 'Lovelace', 'ada@acme.test', 'Active', 1, false);

        INSERT INTO identity."user"
          (id, tenant_id, company_id, public_id, email, role, status, employee_id)
          VALUES ('22222222-2222-2222-2222-222222222222', 1, 1, 'usr_a', 'a@b.c', 'admin',
                  'Active', '11111111-1111-1111-1111-111111111111');

        INSERT INTO identity.session
          (id, tenant_id, user_id, mfa_satisfied, created_at, last_seen_at, absolute_expiry)
          VALUES ('33333333-3333-3333-3333-333333333333', 1,
                  '22222222-2222-2222-2222-222222222222', true, now(), now(),
                  now() + interval '1 day');
        """;
}

[CollectionDefinition(nameof(PrivilegeCollection))]
public sealed class PrivilegeCollection : ICollectionFixture<PrivilegeFixture>;
