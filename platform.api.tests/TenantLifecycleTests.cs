using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Tenants;
using AppPlatform.Platform.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AppPlatform.Platform.Tests;

public class TenantLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly Mock<ITenantService> _tenants = new();
    private readonly List<PlatformAuditLog> _audit = [];
    private readonly Guid _operator = Guid.NewGuid();

    public TenantLifecycleTests()
        => _tenants.Setup(t => t.Audit(It.IsAny<PlatformAuditLog>()))
            .Callback<PlatformAuditLog>(_audit.Add);

    private Tenant Existing(TenantStatus status = TenantStatus.Active)
    {
        var tenant = new Tenant { Id = 1, PublicId = PublicId.New("ten").ToString(), Name = "Acme", Status = status };
        _tenants.Setup(t => t.FindByPublicIdAsync(tenant.PublicId, default)).ReturnsAsync(tenant);
        return tenant;
    }

    private SetTenantStatusFeature.SetTenantStatusCommandHandler Status()
        => new(_tenants.Object, new FakeTimeProvider(Now));

    private SetEntitlementFeature.SetEntitlementCommandHandler Entitlement()
        => new(_tenants.Object, new FakeTimeProvider(Now));

    [Fact]
    public async Task Suspending_changes_status_and_writes_an_audit_entry_in_the_same_save()
    {
        var tenant = Existing();

        var result = await Status().Handle(_operator, tenant.PublicId, new("suspended", "unpaid"));

        Assert.True(result.Succeeded);
        Assert.Equal(TenantStatus.Suspended, tenant.Status);
        Assert.Equal(Now, tenant.StatusChangedAt);

        // Staged together: a status change committed without its trail would be an
        // unattributable act by an operator over someone else's business.
        var entry = Assert.Single(_audit);
        Assert.Equal("tenant.suspended", entry.Action);
        Assert.Equal(_operator, entry.PlatformUserId);
        _tenants.Verify(t => t.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task Resuming_a_suspended_tenant_works()
    {
        var tenant = Existing(TenantStatus.Suspended);

        var result = await Status().Handle(_operator, tenant.PublicId, new("active", null));

        Assert.True(result.Succeeded);
        Assert.Equal(TenantStatus.Active, tenant.Status);
    }

    [Fact]
    public async Task A_retired_tenant_cannot_be_reactivated()
    {
        var tenant = Existing(TenantStatus.Retired);

        var result = await Status().Handle(_operator, tenant.PublicId, new("active", null));

        // Retired is terminal: reviving one resurrects an identity whose sessions were revoked
        // and whose data may already have been purged on that promise.
        Assert.Equal(PlatformProblems.TenantRetired, result.ProblemCode);
        Assert.Equal(TenantStatus.Retired, tenant.Status);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task Setting_the_status_it_already_has_is_reported_rather_than_silently_succeeding()
    {
        var tenant = Existing(TenantStatus.Suspended);

        var result = await Status().Handle(_operator, tenant.PublicId, new("suspended", null));

        // Showing success and writing a fresh audit entry would imply something happened.
        Assert.Equal(PlatformProblems.AlreadyInThatState, result.ProblemCode);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task An_id_of_the_wrong_kind_is_refused_before_any_lookup()
    {
        var result = await Status().Handle(_operator, PublicId.New("emp").ToString(), new("suspended", null));

        Assert.Equal(CommandOutcome.NotFound, result.Outcome);
        _tenants.Verify(t => t.FindByPublicIdAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Theory]
    [InlineData("999")]   // undefined: would be persisted as a state nothing can interpret
    [InlineData("0")]     // defined, but an undocumented alias for Active
    [InlineData("-1")]
    [InlineData("Suspend")]
    [InlineData("")]
    public async Task A_status_that_is_not_one_of_the_documented_names_is_refused(string status)
    {
        var tenant = Existing();

        // Enum.TryParse accepts numeric strings. "999" yields an undefined value; "0" silently
        // means Active and changes meaning the day someone reorders the enum. Matching by name
        // closes both.
        var result = await Status().Handle(_operator, tenant.PublicId, new(status, null));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task Granting_an_app_creates_a_live_entitlement()
    {
        var tenant = Existing();
        TenantApp? granted = null;
        _tenants.Setup(t => t.Add(It.IsAny<TenantApp>())).Callback<TenantApp>(g => granted = g);

        var result = await Entitlement().Handle(_operator, tenant.PublicId, new("tickets", true));

        Assert.True(result.Succeeded);
        Assert.Equal("tickets", granted!.App);
        Assert.Null(granted.RevokedAt);
        Assert.Equal("entitlement.granted", Assert.Single(_audit).Action);
    }

    [Fact]
    public async Task Revoking_marks_the_grant_revoked_rather_than_deleting_it()
    {
        var tenant = Existing();
        var existing = new TenantApp { TenantId = 1, App = "tickets", GrantedAt = Now.AddDays(-30) };
        _tenants.Setup(t => t.FindGrantAsync(1, "tickets", default)).ReturnsAsync(existing);

        await Entitlement().Handle(_operator, tenant.PublicId, new("tickets", false));

        // The grant history is the commercial record of what a customer had and when.
        Assert.Equal(Now, existing.RevokedAt);
    }

    [Fact]
    public async Task Licensed_must_be_stated_explicitly()
    {
        var tenant = Existing();

        var result = await Entitlement().Handle(_operator, tenant.PublicId, new("tickets", null));

        // Both defaults are wrong in a way the caller cannot see: true grants silently, false
        // revokes silently.
        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.Contains("no safe default", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Core_cannot_be_licensed_because_it_is_always_on()
    {
        var tenant = Existing();

        var result = await Entitlement().Handle(_operator, tenant.PublicId, new("core", true));

        // An entitlement table that could express "core revoked" invites someone to try it.
        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.DoesNotContain("core", SetEntitlementFeature.LicensableApps);
    }

    [Fact]
    public async Task Granting_an_app_the_tenant_already_has_is_a_conflict()
    {
        var tenant = Existing();
        _tenants.Setup(t => t.FindGrantAsync(1, "tickets", default))
            .ReturnsAsync(new TenantApp { TenantId = 1, App = "tickets" });

        var result = await Entitlement().Handle(_operator, tenant.PublicId, new("tickets", true));

        Assert.Equal(PlatformProblems.AppAlreadyLicensed, result.ProblemCode);
    }
}
