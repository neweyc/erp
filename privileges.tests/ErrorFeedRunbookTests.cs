using AppPlatform.Boundary;
using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The error feed's two migrations, applied the way database/README.md says a deployment applies
/// them, with the roles it names: the table as ap_platform_migrate, the published function as
/// ap_owner. Everything else in this suite applies migrations as ap_owner, which would hide a
/// migration that only works as ap_owner.
///
/// Its own container, starting from nothing, so the roles are the only thing deciding the outcome.
/// </summary>
public sealed class ErrorFeedRunbookTests : IAsyncLifetime
{
    private const string TableMigration = "20260929203443_AddErrorFeed";
    private const string FunctionScript = "20260929203446_AddErrorFeedPublishedFunction.sql";

    private readonly Testcontainers.PostgreSql.PostgreSqlContainer _container =
        new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:16-alpine").WithDatabase("appplatform").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Connected as the superuser, but acting as <paramref name="role"/> from the start.</summary>
    private string As(string role)
        => new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Options = $"-c role={role}" }.ConnectionString;

    private static async Task RunScriptAsync(string connectionString, string path)
    {
        // psql meta-commands (\\set) are for psql; the SQL around them is what matters here.
        var sql = string.Join('\n', (await File.ReadAllLinesAsync(path)).Where(l => !l.TrimStart().StartsWith('\\')));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Privileges(string file) => Path.Combine(RepositoryPaths.Project("database/privileges"), file);

    [Fact]
    public async Task The_table_applies_as_the_migration_role_and_the_function_only_as_ap_owner()
    {
        var superuser = _container.GetConnectionString();
        await RunScriptAsync(superuser, Privileges("00-roles.sql"));
        await RunScriptAsync(superuser, Privileges("01-schemas.sql"));

        // Every platform migration up to and including the table, as the platform's migration role.
        await using (var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(As("ap_platform_migrate"), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options))
        {
            await db.GetService<IMigrator>().MigrateAsync(TableMigration);
        }

        var functionScript = Path.Combine(RepositoryPaths.Project("database/platform"), FunctionScript);

        // The published function is NOT for the migration role, which is why the runbook names it.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => RunScriptAsync(As("ap_platform_migrate"), functionScript));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);

        await RunScriptAsync(As("ap_owner"), functionScript);

        // The other services, as a deployment has them; 02-grants.sql grants on their objects too.
        // Applied as ap_owner as init-local.sh does: this test is about the platform's two.
        foreach (var service in new[] { "core", "tickets", "ledger" })
            await RunScriptAsync(As("ap_owner"), Path.Combine(RepositoryPaths.Project($"database/{service}"), "migrations-all.sql"));

        await RunScriptAsync(superuser, Privileges("02-grants.sql"));

        // The boundary holds: 99-verify reports nothing, including the new function's ownership.
        await using (var verify = new NpgsqlConnection(superuser))
        {
            await verify.OpenAsync();
            var sql = string.Join('\n', (await File.ReadAllLinesAsync(Privileges("99-verify.sql"))).Where(l => !l.TrimStart().StartsWith('\\')));
            await using var command = new NpgsqlCommand(sql, verify);
            await using var reader = await command.ExecuteReaderAsync();
            var findings = new List<string>();
            while (await reader.ReadAsync()) findings.Add($"{reader.GetString(0)}: {reader.GetString(1)}");
            Assert.Empty(findings);
        }

        // And a service can use it: the tickets role adds an occurrence.
        await using (var tickets = new NpgsqlConnection(As("ap_tickets_rt")))
        {
            await tickets.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT platform_v1.record_error('err_0000000000000000000000001', '0123456789abcdef', 'tickets', NULL, 1, now())",
                tickets);
            await command.ExecuteNonQueryAsync();
        }
    }
}
