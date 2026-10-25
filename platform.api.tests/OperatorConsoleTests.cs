using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Audit;
using AppPlatform.Platform.Features.Auth;
using AppPlatform.Platform.Features.Tenants;
using AppPlatform.Platform.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AppPlatform.Platform.Tests;

/// <summary>The reads and the sign-out the operator console is built on.</summary>
public class OperatorConsoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly Mock<ITenantService> _tenants = new();
    private readonly Mock<IAuditLogService> _audit = new();

    [Fact]
    public async Task A_tenant_shows_its_live_apps_from_the_same_history_it_lists()
    {
        var tenant = new Tenant { Id = 7, PublicId = PublicId.New("ten").ToString(), Name = "Acme" };
        _tenants.Setup(t => t.FindByPublicIdAsync(tenant.PublicId, default)).ReturnsAsync(tenant);
        _tenants.Setup(t => t.GrantHistoryAsync(7, default)).ReturnsAsync([
            new GrantRow("tickets", Now, null),
            new GrantRow("ledger", Now.AddDays(-2), Now.AddDays(-1)),
            new GrantRow("ledger", Now.AddDays(-5), Now.AddDays(-3)),
        ]);
        _audit.Setup(a => a.RecentAsync(tenant.PublicId, GetTenantFeature.RecentActions, default)).ReturnsAsync([]);

        var result = await new GetTenantFeature.GetTenantQueryHandler(_tenants.Object, _audit.Object).Handle(tenant.PublicId);

        var model = Assert.IsType<GetTenantFeature.TenantDetailModel>(result.Value);
        // Revoked grants stay in the history but are not licensed.
        Assert.Equal(["tickets"], model.Apps);
        Assert.Equal(3, model.Grants.Count);
        Assert.Equal("Active", model.Status);
    }

    [Theory]
    [InlineData("emp_7h2k3m4n5p6q7r8s9t0v1")]
    [InlineData("not-an-id")]
    public async Task Something_that_is_not_a_tenant_id_is_not_found_without_a_lookup(string id)
    {
        var result = await new GetTenantFeature.GetTenantQueryHandler(_tenants.Object, _audit.Object).Handle(id);

        Assert.Equal(PlatformProblems.NotFound, result.ProblemCode);
        _tenants.Verify(t => t.FindByPublicIdAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task An_unknown_tenant_is_not_found()
    {
        var result = await new GetTenantFeature.GetTenantQueryHandler(_tenants.Object, _audit.Object)
            .Handle(PublicId.New("ten").ToString());

        Assert.Equal(CommandOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData(null, GetOperatorAuditFeature.DefaultLimit)]
    [InlineData(0, 1)]
    [InlineData(100_000, GetOperatorAuditFeature.MaxLimit)]
    public async Task The_audit_limit_is_clamped_and_covers_every_tenant(int? asked, int used)
    {
        _audit.Setup(a => a.RecentAsync(It.IsAny<string?>(), It.IsAny<int>(), default)).ReturnsAsync([]);

        await new GetOperatorAuditFeature.GetOperatorAuditQueryHandler(_audit.Object).Handle(asked);

        _audit.Verify(a => a.RecentAsync(null, used, default), Times.Once);
    }

    [Fact]
    public async Task Signing_out_revokes_the_session_on_the_server_and_is_audited()
    {
        var sessions = new Mock<IOperatorSessionStore>();
        var writer = new Mock<ITenantAuditWriter>();
        var written = new List<PlatformAuditLog>();
        writer.Setup(w => w.Write(It.IsAny<PlatformAuditLog>())).Callback<PlatformAuditLog>(written.Add);
        var principal = new OperatorPrincipal { PlatformUserId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Email = "op@x.test" };

        await new SignOutFeature.SignOutCommandHandler(sessions.Object, writer.Object, new FakeTimeProvider(Now))
            .Handle(principal);

        // A copied cookie must stop working, not only the one the browser just deleted.
        sessions.Verify(s => s.RevokeAsync(principal.SessionId, Now, default), Times.Once);
        var entry = Assert.Single(written);
        Assert.Equal("operator.signed_out", entry.Action);
        Assert.Equal(principal.PlatformUserId, entry.PlatformUserId);
        writer.Verify(w => w.FlushAsync(default), Times.Once);
    }
}
