using AppPlatform.Boundary;
using AppPlatform.Ids;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// Upgrading a ledger that already posted entries through <c>AddPeriodClose</c>, using the GENERATED
/// script Chris applies. That migration backfills a <c>ledger.books</c> row, with a public id minted in
/// SQL, for every company that had posted. The shared fixture never runs the backfill (it seeds after
/// every migration), which is how D16 shipped: the ids were hex from md5, not the pinned format.
/// </summary>
public class LedgerBooksUpgradeTests : IAsyncLifetime
{
    private const string PeriodCloseMigration = "20260925211934_AddPeriodClose";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("appplatform")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Backfilled_books_get_public_ids_in_the_pinned_format()
    {
        var privileges = RepositoryPaths.Project("database/privileges");
        await ExecuteFileAsync(Path.Combine(privileges, "00-roles.sql"));
        await ExecuteFileAsync(Path.Combine(privileges, "01-schemas.sql"));
        await ExecuteFileAsync(Path.Combine(RepositoryPaths.Project("database/platform"), "migrations-all.sql"), "ap_owner");
        await ExecuteFileAsync(Path.Combine(RepositoryPaths.Project("database/core"), "migrations-all.sql"), "ap_owner");

        // The ledger before AddPeriodClose: the idempotent script up to that migration's first block.
        var ledgerScript = await File.ReadAllTextAsync(
            Path.Combine(RepositoryPaths.Project("database/ledger"), "migrations-all.sql"));
        var cut = ledgerScript.LastIndexOf("DO $EF$", ledgerScript.IndexOf(PeriodCloseMigration, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(cut > 0, "the script should contain AddPeriodClose");
        await ExecuteAsync($"SET ROLE ap_owner;\n{ledgerScript[..cut]}\nCOMMIT;");

        // Entries already posted by three companies across two tenants. Triggers and foreign keys are
        // switched off for the insert (replica mode): this test is about the books rows the upgrade
        // creates, not about posting, and a balanced entry needs accounts, lines and a tenant.
        await ExecuteAsync("""
            SET session_replication_role = replica;
            INSERT INTO ledger.journal_entry
              (id, tenant_id, company_id, public_id, number, fiscal_year, entry_date, memo, currency, posted_at, line_count)
            VALUES
              (gen_random_uuid(), 1, 1, 'je_a', 1, 2026, '2026-09-01', 'a', 'USD', now(), 2),
              (gen_random_uuid(), 1, 1, 'je_b', 2, 2026, '2026-09-02', 'b', 'USD', now(), 2),
              (gen_random_uuid(), 1, 2, 'je_c', 1, 2026, '2026-09-01', 'c', 'USD', now(), 2),
              (gen_random_uuid(), 2, 3, 'je_d', 1, 2026, '2026-09-01', 'd', 'USD', now(), 2);
            """);

        // The upgrade: the whole script again. It is idempotent, so only AddPeriodClose runs now.
        await ExecuteAsync($"SET ROLE ap_owner;\n{ledgerScript}");

        var ids = await QueryAsync("SELECT public_id FROM ledger.books ORDER BY company_id");

        // One books row per company that had posted, each with its own id.
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            Assert.True(PublicId.TryParse(id, "bk", out var parsed), $"{id} should parse as a books id");
            // Canonical: what is stored is what PublicId writes, with nothing folded on the way in.
            Assert.Equal(id, parsed.ToString());
        });

        // Hex (the D16 ids) parses too, since it is a subset of the alphabet, so the check above
        // alone cannot tell the two apart. Uses beyond 0-f can: 75 characters all within hex
        // would happen by chance once in 2^75.
        Assert.Contains(string.Concat(ids.Select(id => id[3..])), c => c is > 'f');
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
