using System.Net;
using System.Text.Json;
using AppPlatform.ErrorFeed;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// The operator error feed against the shipped schema and grants: customer services may add to it,
/// through platform_v1.record_error only, and may not read it.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class ErrorFeedTests(PrivilegeFixture fixture)
{
    private static readonly string[] CustomerRoles = ["ap_core_rt", "ap_tickets_rt", "ap_ledger_rt"];

    private static string Reference() => ErrorReference.New();

    private async Task RecordAsAsync(string role, string reference, string fingerprint = "0123456789abcdef",
        string app = "tickets", int count = 1, DateTimeOffset? occurredAt = null, int? tenantId = null)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringAs(role));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT platform_v1.record_error($1, $2, $3, $6, $4, $5)", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = reference });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = fingerprint });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = app });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = occurredAt ?? DateTimeOffset.UtcNow });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)tenantId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string reference)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM platform.error_occurrence WHERE reference = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = reference });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData("ap_core_rt")]
    [InlineData("ap_tickets_rt")]
    [InlineData("ap_ledger_rt")]
    [InlineData("ap_platform_rt")]
    public async Task Every_service_can_add_to_the_feed_through_the_function(string role)
    {
        var reference = Reference();

        await RecordAsAsync(role, reference);

        Assert.Equal(1, await CountAsync(reference));
    }

    [Theory]
    [InlineData("SELECT count(*) FROM platform.error_occurrence")]
    [InlineData("INSERT INTO platform.error_occurrence (reference, fingerprint, app, count, occurred_at) VALUES ('err_x', '0123456789abcdef', 'x', 1, now())")]
    public async Task No_customer_service_can_read_the_feed_or_write_it_directly(string sql)
    {
        // The feed shows which tenants are failing: not one tenant's services' business. And a
        // direct insert would skip the function's checks on what may be stored.
        foreach (var role in CustomerRoles)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionStringAs(role));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);

            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task A_tenant_id_that_does_not_exist_is_accepted_exactly_like_one_that_does()
    {
        // No foreign key, deliberately: were a report for a missing tenant refused, any service
        // could learn which tenant ids exist by trying them.
        var forMissingTenant = Reference();

        await RecordAsAsync("ap_tickets_rt", forMissingTenant, tenantId: 987_654_321);

        Assert.Equal(1, await CountAsync(forMissingTenant));
    }

    [Theory]
    [InlineData("not-a-reference", "0123456789abcdef", "tickets", 1, 0)]
    [InlineData(null, "customer ada@example.com", "tickets", 1, 0)] // free text where a fingerprint goes
    [InlineData(null, "0123456789abcdef", "Tickets; drop", 1, 0)]
    [InlineData(null, "0123456789abcdef", "tickets", 0, 0)]
    [InlineData(null, "0123456789abcdef", "tickets", 1, 3)] // three days in the future
    public async Task Anything_but_the_allowed_shapes_is_refused_not_stored(
        string? reference, string fingerprint, string app, int count, int daysAhead)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => RecordAsAsync(
            "ap_tickets_rt", reference ?? Reference(), fingerprint, app, count, DateTimeOffset.UtcNow.AddDays(daysAhead)));

        Assert.Equal(PostgresErrorCodes.InvalidParameterValue, error.SqlState);
    }

    [Fact]
    public async Task An_unhandled_exception_in_a_service_arrives_in_the_feed_under_the_reference_the_customer_saw()
    {
        // The whole path in-process: middleware, logger, queue, writer, and the function called by
        // a real runtime role — the tickets service's own, with no grant on the table.
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddErrorFeed(app: "tickets", fixture.ConnectionStringAs("ap_tickets_rt"));
                })
                .Configure(app =>
                {
                    app.UseErrorFeed();
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapGet("/boom", (HttpContext _) => throw new InvalidOperationException("boom")));
                }))
            .StartAsync();

        using var response = await host.GetTestClient().GetAsync("/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var reference = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("reference").GetString()!;

        // Delivered by a background writer, so allowed a moment to arrive.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await CountAsync(reference) == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);

        Assert.Equal(1, await CountAsync(reference));
    }
}
