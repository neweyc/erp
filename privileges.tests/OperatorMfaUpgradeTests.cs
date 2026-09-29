using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Upgrading a live platform database to mandatory MFA.
///
/// Every operator session that existed before AddDataKeysAndOperatorTotp was signed in with a
/// password alone, and is marked MFA-satisfied because nothing else existed then. Left alone, those
/// sessions would keep working after MFA became mandatory, for as long as they last. The migration
/// revokes them; this proves it on a database that actually holds one, which a fresh bootstrap never
/// does.
///
/// Its own container, because the shared privilege database is already at the latest migration.
/// </summary>
public sealed class OperatorMfaUpgradeTests : IAsyncLifetime
{
    private const string BeforeMfa = "20260925015103_AddTenantProvisioningKey";
    private const string MandatoryMfa = "20260929182954_AddDataKeysAndOperatorTotp";

    private readonly Testcontainers.PostgreSql.PostgreSqlContainer _container =
        new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:16-alpine").WithDatabase("appplatform").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task A_password_only_session_from_before_mandatory_MFA_is_revoked_by_the_upgrade()
    {
        var connection = _container.GetConnectionString();
        // Configured as the service's migrations are, history table included.
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(BeforeMfa);

        var sessionId = Guid.NewGuid();
        await using (var sql = new NpgsqlConnection(connection))
        {
            await sql.OpenAsync();
            var userId = Guid.NewGuid();
            await using var command = new NpgsqlCommand(
                "INSERT INTO platform.platform_user (id, public_id, email, password_hash, active, totp_secret_version, created_at) " +
                $"VALUES ('{userId}', 'op_upgrade', 'op@upgrade.test', 'x', true, 0, now()); " +
                "INSERT INTO platform.platform_session (id, platform_user_id, mfa_satisfied, created_at, last_seen_at, absolute_expiry) " +
                $"VALUES ('{sessionId}', '{userId}', true, now(), now(), now() + interval '8 hours');",
                sql);
            await command.ExecuteNonQueryAsync();
        }

        // To THIS migration, not the latest: later ones grant to runtime roles, which this bare
        // container (no privilege scripts) does not have, and they are not what is under test.
        await migrator.MigrateAsync(MandatoryMfa);

        var session = await db.PlatformSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.NotNull(session.RevokedAt);
    }
}
