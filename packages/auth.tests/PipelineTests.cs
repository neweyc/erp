using System.Net;

namespace AppPlatform.Auth.Tests;

/// <summary>
/// The wiring, not the rules. Middleware order and the caller-to-tenant bridge cannot be
/// proved by unit tests over the pure policies, and both fail in ways that look like
/// nothing is wrong.
/// </summary>
public class PipelineTests
{
    [Fact]
    public async Task An_authenticated_request_reaches_the_endpoint()
    {
        var probe = await Pipeline.SendAsync(Build.Session(), sessionId: Pipeline.SessionId);

        Assert.Equal(HttpStatusCode.OK, probe.Status);
    }

    [Fact]
    public async Task The_authenticated_tenant_is_what_tenancy_resolves()
    {
        // THE integration. Query filters and insert stamping read ITenantProvider, and this
        // asserts it returns the tenant from the validated session — the same object the
        // authentication middleware populated, with no second path that could set it.
        var probe = await Pipeline.SendAsync(Build.Session(), sessionId: Pipeline.SessionId);

        Assert.Equal(1, probe.TenantIdSeenByTenancy);
    }

    [Fact]
    public async Task An_anonymous_request_has_no_tenant_rather_than_every_tenant()
    {
        // Not a failure — sign-in and invite acceptance are anonymous. What matters is that
        // tenancy resolves to nothing, so a query returns no rows instead of all rows.
        var probe = await Pipeline.SendAsync(stored: null, sessionId: null);

        Assert.Equal(HttpStatusCode.OK, probe.Status);
        Assert.Null(probe.TenantIdSeenByTenancy);
        Assert.Equal("none", probe.Body);
    }

    [Theory]
    [InlineData("session_revoked")]
    public async Task A_revoked_session_is_rejected_with_its_own_code(string expected)
    {
        var probe = await Pipeline.SendAsync(
            Build.Session(revokedAt: Build.Now.AddMinutes(-1)), sessionId: Pipeline.SessionId);

        Assert.Equal(HttpStatusCode.Unauthorized, probe.Status);
        Assert.Contains(expected, probe.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_session_is_rejected_and_never_reaches_the_endpoint()
    {
        var probe = await Pipeline.SendAsync(stored: null, sessionId: Pipeline.SessionId);

        Assert.Equal(HttpStatusCode.Unauthorized, probe.Status);
        Assert.Contains("session_invalid", probe.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suspended_tenant_is_403_on_an_ordinary_route_and_keeps_its_principal()
    {
        var probe = await Pipeline.SendAsync(
            Build.Session(status: TenantStatus.Suspended), sessionId: Pipeline.SessionId);

        // 403, not 401: authentication succeeded. A 401 here would mean the principal was
        // discarded, and the export below could not work.
        Assert.Equal(HttpStatusCode.Forbidden, probe.Status);
        Assert.Contains("tenant_suspended", probe.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suspended_tenant_can_still_reach_the_data_export()
    {
        var probe = await Pipeline.SendAsync(
            Build.Session(status: TenantStatus.Suspended),
            path: "/api/core/v1/tenant/export",
            sessionId: Pipeline.SessionId);

        Assert.Equal(HttpStatusCode.OK, probe.Status);
        Assert.Equal(1, probe.TenantIdSeenByTenancy);
    }

    [Fact]
    public async Task A_retired_tenant_fails_authentication_outright()
    {
        var probe = await Pipeline.SendAsync(
            Build.Session(status: TenantStatus.Retired),
            path: "/api/core/v1/tenant/export",
            sessionId: Pipeline.SessionId);

        // Even the export is gone: retired is terminal, unlike suspended.
        Assert.Equal(HttpStatusCode.Unauthorized, probe.Status);
        Assert.Contains("tenant_retired", probe.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Unsafe_methods_are_refused_without_a_csrf_token(string method)
    {
        var probe = await Pipeline.SendAsync(
            Build.Session(), method: method, sessionId: Pipeline.SessionId);

        Assert.Equal(HttpStatusCode.Forbidden, probe.Status);
        Assert.Contains("csrf_failed", probe.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unsafe_method_with_matching_tokens_proceeds()
    {
        var probe = await Pipeline.SendAsync(
            Build.Session(), method: "POST", sessionId: Pipeline.SessionId,
            csrfHeader: "tok-abc", csrfCookie: "tok-abc");

        Assert.Equal(HttpStatusCode.OK, probe.Status);
    }

    [Fact]
    public async Task Csrf_runs_after_authentication_so_it_knows_the_principal_kind()
    {
        // Ordering check. If CSRF ran first it could not tell a cookie-authenticated
        // request from a key-authenticated one, and would have to demand a token from
        // integrations that cannot supply one.
        var probe = await Pipeline.SendAsync(stored: null, method: "POST", sessionId: null);

        Assert.Equal(HttpStatusCode.OK, probe.Status);
    }

    [Fact]
    public async Task Csrf_is_refused_before_suspension_is_considered()
    {
        var probe = await Pipeline.SendAsync(
            Build.Session(status: TenantStatus.Suspended), method: "POST", sessionId: Pipeline.SessionId);

        Assert.Equal(HttpStatusCode.Forbidden, probe.Status);
        Assert.Contains("csrf_failed", probe.Body, StringComparison.Ordinal);
    }
}
