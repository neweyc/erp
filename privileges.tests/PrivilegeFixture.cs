using AppPlatform.Boundary;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// A throwaway PostgreSQL with the real privilege scripts applied, in the order the
/// runbook specifies: roles and default privileges, then migrations, then grants.
///
/// The ordering is part of what is under test. Default privileges apply only to objects
/// created after they are set, so running 01 after the migrations would leave every table
/// ungranted — and that is a mistake this suite should catch rather than replicate.
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
        await ExecuteFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-migrations.sql"));
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
    private async Task ExecuteFileAsync(string path)
    {
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(path))
            .Where(line => !line.TrimStart().StartsWith('\\')));

        await ExecuteAsync(sql);
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

    private const string SeedSql = """
        INSERT INTO platform.tenant (public_id, name, status) VALUES ('ten_a', 'Acme', 'active');
        INSERT INTO platform.tenant_app VALUES (1, 'tickets');
        INSERT INTO core.company (tenant_id, name) VALUES (1, 'Acme Ltd');
        INSERT INTO core.employee
          VALUES ('11111111-1111-1111-1111-111111111111', 1, 1, 'emp_x', 'Ada L', '555');
        INSERT INTO identity."user"
          VALUES ('22222222-2222-2222-2222-222222222222', 1, 1, 'a@b.c', 'admin',
                  '11111111-1111-1111-1111-111111111111', true);
        INSERT INTO identity.session
          VALUES ('33333333-3333-3333-3333-333333333333',
                  '22222222-2222-2222-2222-222222222222', now(), null,
                  now() + interval '1 day', true);
        """;
}

[CollectionDefinition(nameof(PrivilegeCollection))]
public sealed class PrivilegeCollection : ICollectionFixture<PrivilegeFixture>;
