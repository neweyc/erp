using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The published contracts are consumed by code that cannot see the tables behind them, so
/// a change to one is a change to every consumer. These tests pin the shape.
///
/// Column ORDER is asserted as well as membership: <c>RETURNS TABLE</c> is positional, so
/// reordering two same-typed columns compiles, deploys, and silently swaps two values.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class PublishedContractTests(PrivilegeFixture fixture)
{
    private async Task<string[]> ColumnsOfAsync(string schema, string view)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = @s AND table_name = @v
            ORDER BY ordinal_position
            """, connection);
        command.Parameters.AddWithValue("s", schema);
        command.Parameters.AddWithValue("v", view);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return [.. columns];
    }

    [Fact]
    public async Task core_v1_employee_exposes_exactly_the_agreed_columns()
    {
        // Deliberately absent: phone. Encrypted and PII columns never reach a published
        // contract — ciphertext cannot be filtered or sorted, and an app has no business
        // holding it.
        Assert.Equal(
            ["id", "tenant_id", "company_id", "public_id", "display_name"],
            await ColumnsOfAsync("core_v1", "employee"));
    }

    [Fact]
    public async Task session_context_returns_every_field_the_evaluator_needs()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        // Read the catalog's own output-parameter list rather than parsing the rendered
        // signature: types like `timestamp with time zone` contain spaces, and any regex
        // over that text shreds them into columns that do not exist.
        await using var command = new NpgsqlCommand(
            """
            SELECT a.name
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace,
            LATERAL unnest(p.proargnames, p.proargmodes) WITH ORDINALITY AS a(name, mode, ord)
            WHERE n.nspname = 'identity_v1' AND p.proname = 'session_context' AND a.mode = 't'
            ORDER BY a.ord
            """, connection);

        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        }

        // Mirrors AppPlatform.Auth.SessionContext. A field the evaluator reads but the
        // function does not return is not a compile error anywhere — it is a null at
        // runtime, in the code that decides who gets in.
        Assert.Equal(
            [
                "session_id", "user_id", "tenant_id", "company_id", "tenant_status",
                "role", "user_active", "mfa_satisfied", "last_seen_at",
                "absolute_expiry", "revoked_at", "employee_id",
                "licensed_apps", "idle_timeout_minutes",
            ],
            columns);
    }

    [Fact]
    public async Task session_context_returns_nothing_for_an_unknown_session()
    {
        // The evaluator treats null as session_invalid. If the function raised instead,
        // every expired cookie would surface as a 500.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM identity_v1.session_context('00000000-0000-0000-0000-000000000000')",
            connection);

        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
