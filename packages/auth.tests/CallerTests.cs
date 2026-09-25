using AppPlatform.Audit;

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

    [Fact]
    public void In_a_request_the_audit_actor_is_the_caller()
    {
        var user = Guid.NewGuid();
        var asUser = new CallerContext();
        asUser.SetCaller(new Caller
        {
            PrincipalId = user, Kind = PrincipalKind.User, UserId = user,
            TenantId = 1, CompanyId = 1, Role = "admin",
        });

        var key = Key();
        var asKey = new CallerContext();
        asKey.SetCaller(key);

        Assert.Equal(AuditActor.User(user), asUser.Current);
        // The KEY is the actor, never a user — an integration's writes must not read as a person's.
        Assert.Equal(AuditActor.ApiKey(key.PrincipalId), asKey.Current);
    }

    [Fact]
    public void A_request_cannot_re_attribute_its_own_writes()
    {
        var context = new CallerContext();
        context.SetCaller(Key());

        Assert.Throws<InvalidOperationException>(
            () => context.UseActor(AuditActor.System("provisioning")));
    }

    [Fact]
    public void Before_a_session_the_declared_actor_is_used_and_absent_means_none()
    {
        var context = new CallerContext();
        Assert.Null(context.Current);

        context.UseActor(AuditActor.System("provisioning"));
        Assert.Equal(AuditActor.System("provisioning"), context.Current);
    }
}
