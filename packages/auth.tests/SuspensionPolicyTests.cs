namespace AppPlatform.Auth.Tests;

public class SuspensionPolicyTests
{
    private static Caller Caller(bool suspended) => new()
    {
        PrincipalId = Guid.NewGuid(),
        Kind = PrincipalKind.User,
        TenantId = 1,
        CompanyId = 1,
        Role = "admin",
        TenantSuspended = suspended,
    };

    [Fact]
    public void An_active_tenant_reaches_everything()
        => Assert.Null(SuspensionPolicy.Evaluate(Caller(suspended: false), "/api/tickets/v1/tickets"));

    [Fact]
    public void A_suspended_tenant_is_refused_ordinary_routes()
        => Assert.Equal(AuthProblem.TenantSuspended,
            SuspensionPolicy.Evaluate(Caller(suspended: true), "/api/tickets/v1/tickets"));

    [Theory]
    [InlineData("/api/core/v1/auth/session")]
    [InlineData("/api/core/v1/auth")]
    [InlineData("/API/CORE/V1/AUTH/SESSION")]
    [InlineData("/api/core/v1/tenant/export")]
    [InlineData("/api/core/v1/tenant/export/download")]
    public void A_suspended_tenant_keeps_auth_and_export(string path)
        => Assert.Null(SuspensionPolicy.Evaluate(Caller(suspended: true), path));

    [Theory]
    [InlineData("/api/core/v1/authorised-signers")]
    [InlineData("/api/core/v1/tenant/exports-report")]
    public void The_allowlist_matches_segments_not_prefixes(string path)
    {
        // A naive StartsWith would let both of these through on the strength of "auth" and
        // "export" — quietly widening a suspended tenant's reach to whatever an endpoint
        // name happens to begin with.
        Assert.Equal(AuthProblem.TenantSuspended, SuspensionPolicy.Evaluate(Caller(suspended: true), path));
    }
}
