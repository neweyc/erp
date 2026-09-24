namespace AppPlatform.Auth.Tests;

public class CallerTests
{
    private static Caller Key() => new()
    {
        PrincipalId = Guid.NewGuid(),
        Kind = PrincipalKind.ApiKey,
        TenantId = 1,
        CompanyId = 1,
        Role = "integration",
        Scopes = ["tickets:read"],
    };

    [Fact]
    public void RequireUserId_returns_the_user_for_an_interactive_caller()
    {
        var userId = Guid.NewGuid();
        var caller = new Caller
        {
            PrincipalId = userId, Kind = PrincipalKind.User, UserId = userId,
            TenantId = 1, CompanyId = 1, Role = "admin",
        };

        Assert.Equal(userId, caller.RequireUserId());
    }

    [Fact]
    public void RequireUserId_throws_for_an_api_key_rather_than_returning_empty()
    {
        // Guid.Empty would be written into a record as though a user with that id had
        // acted, which is how a machine action becomes attributed to a person.
        var ex = Assert.Throws<InvalidOperationException>(() => Key().RequireUserId());

        Assert.Contains("interactive user", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_api_key_caller_carries_no_user_id()
        => Assert.Null(Key().UserId);
}
