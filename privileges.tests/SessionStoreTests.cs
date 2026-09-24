using AppPlatform.Auth;
using Npgsql;

namespace AppPlatform.PrivilegeTests;

/// <summary>
/// End to end for session resolution: the real SECURITY DEFINER function, reached over a
/// connection authenticated as a real runtime role, mapped by the real store, judged by
/// the real evaluator.
///
/// Every layer of that is covered elsewhere in isolation. What only this can catch is the
/// seam — a column renamed in SQL, a grant that was never issued, a null arriving where
/// the evaluator expects a value.
/// </summary>
[Collection(nameof(PrivilegeCollection))]
public class SessionStoreTests(PrivilegeFixture fixture)
{
    private static readonly Guid KnownSession = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private async Task<SessionContext?> ReadAsAsync(string role, Guid sessionId)
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionStringAs(role));
        return await new NpgsqlSessionStore(dataSource).FindAsync(sessionId);
    }

    [Fact]
    public async Task A_customer_role_resolves_a_session_through_the_granted_function()
    {
        var context = await ReadAsAsync("ap_core_rt", KnownSession);

        Assert.NotNull(context);
        Assert.Equal(1, context.TenantId);
        Assert.Equal("admin", context.Role);
        Assert.Equal(TenantStatus.Active, context.TenantStatus);
        Assert.True(context.UserActive);
        Assert.Equal(["tickets"], context.LicensedApps);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), context.EmployeeId);
        Assert.Null(context.RevokedAt);
        Assert.Null(context.IdleTimeoutMinutes);
    }

    [Fact]
    public async Task An_app_role_resolves_the_same_session()
    {
        // An app authenticates its own callers; it does not ask core to do it. That is why
        // EXECUTE is granted to every customer role rather than to core alone.
        var context = await ReadAsAsync("ap_tickets_rt", KnownSession);

        Assert.NotNull(context);
        Assert.Equal("admin", context.Role);
    }

    [Fact]
    public async Task An_unknown_session_returns_null_rather_than_throwing()
    {
        // The evaluator reads null as session_invalid. If this threw, every expired cookie
        // would surface to the user as a 500.
        Assert.Null(await ReadAsAsync("ap_core_rt", Guid.Parse("00000000-0000-0000-0000-000000000000")));
    }

    [Fact]
    public async Task A_resolved_session_evaluates_to_a_caller()
    {
        var context = await ReadAsAsync("ap_core_rt", KnownSession);

        var result = SessionEvaluator.Evaluate(context, "admin", DateTimeOffset.UtcNow);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Caller!.TenantId);
        Assert.Equal(PrincipalKind.User, result.Caller.Kind);
    }

    [Fact]
    public async Task The_platform_role_cannot_resolve_a_session_at_all()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionStringAs("ap_platform_rt"));

        // Not "returns nothing" — cannot execute. Operator sessions live in platform
        // tables; the platform resolving a tenant session would undo its isolation.
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => new NpgsqlSessionStore(dataSource).FindAsync(KnownSession));

        Assert.Equal("42501", ex.SqlState);
    }

    [Theory]
    [InlineData("ap_core_rt")]
    [InlineData("ap_tickets_rt")]
    public async Task Touching_updates_last_seen_through_the_granted_function(string role)
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionStringAs(role));
        var store = new NpgsqlSessionStore(dataSource);

        var before = (await store.FindAsync(KnownSession))!.LastSeenAt;
        await store.TouchAsync(KnownSession);
        var after = (await store.FindAsync(KnownSession))!.LastSeenAt;

        // In particular, the app role has no access to the identity schema or table.
        Assert.True(after > before, $"last_seen_at did not advance ({before} -> {after})");
    }
}
