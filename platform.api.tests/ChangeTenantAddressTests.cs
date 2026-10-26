using AppPlatform.Api;
using AppPlatform.Ids;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Tenants;
using AppPlatform.Platform.Services;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Time.Testing;
using Moq;
using static AppPlatform.Platform.Features.Tenants.ChangeTenantAddressFeature;

namespace AppPlatform.Platform.Tests;

public class ChangeTenantAddressTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly Mock<ITenantService> _tenants = new();
    private readonly Mock<IDbContextTransaction> _transaction = new();
    private readonly Guid _operator = Guid.NewGuid();
    private readonly List<PlatformAuditLog> _audit = [];
    private readonly List<TenantSubdomain> _added = [];
    private readonly List<TenantAddressPeriod> _periods = [];
    private readonly Tenant _tenant = new() { Id = 7, PublicId = PublicId.New("ten").ToString(), Name = "Acme" };
    private readonly TenantSubdomain _current = new() { Subdomain = "acme", TenantId = 7, AssignedAt = Now.AddDays(-30) };

    public ChangeTenantAddressTests()
    {
        _tenants.Setup(t => t.FindByPublicIdAsync(_tenant.PublicId, default)).ReturnsAsync(_tenant);
        _tenants.Setup(t => t.CurrentAddressAsync(7, default)).ReturnsAsync(_current);
        _tenants.Setup(t => t.BeginTransactionAsync(default)).ReturnsAsync(_transaction.Object);
        _tenants.Setup(t => t.TrySaveAsync(default)).ReturnsAsync(true);
        _tenants.Setup(t => t.Audit(It.IsAny<PlatformAuditLog>())).Callback<PlatformAuditLog>(_audit.Add);
        _tenants.Setup(t => t.Add(It.IsAny<TenantSubdomain>())).Callback<TenantSubdomain>(_added.Add);
        _tenants.Setup(t => t.Add(It.IsAny<TenantAddressPeriod>())).Callback<TenantAddressPeriod>(_periods.Add);
    }

    private Task<CommandResult> Change(string? subdomain)
        => new ChangeTenantAddressCommandHandler(_tenants.Object, new FakeTimeProvider(Now))
            .Handle(_operator, _tenant.PublicId, new(subdomain));

    [Fact]
    public async Task Moves_the_tenant_to_a_new_address_keeping_the_old_one_reserved()
    {
        var result = await Change("Acme-Fire");

        Assert.True(result.Succeeded);
        Assert.Equal(Now, _current.ReplacedAt);
        var added = Assert.Single(_added);
        Assert.Equal("acme-fire", added.Subdomain);
        Assert.Equal(7, added.TenantId);
        Assert.Null(added.ReplacedAt);
        _transaction.Verify(t => t.CommitAsync(default), Times.Once);
    }

    [Fact]
    public async Task Records_the_span_the_old_address_was_in_use()
    {
        await Change("acme-fire");

        var period = Assert.Single(_periods);
        Assert.Equal(("acme", 7, Now.AddDays(-30), Now), (period.Subdomain, period.TenantId, period.AssignedAt, period.ReplacedAt));
    }

    [Fact]
    public async Task Retires_the_old_address_in_its_own_save_before_adding_the_new_one()
    {
        // The database allows one current address per tenant, so the old one must be written as
        // replaced before the new one is inserted.
        var order = new List<string>();
        _tenants.Setup(t => t.SaveAsync(default)).Callback(() => order.Add("retire old")).Returns(Task.CompletedTask);
        _tenants.Setup(t => t.TrySaveAsync(default)).Callback(() => order.Add("add new")).ReturnsAsync(true);

        await Change("acme-fire");

        Assert.Equal(["retire old", "add new"], order);
    }

    [Fact]
    public async Task Is_audited_with_the_operator_and_both_addresses()
    {
        await Change("acme-fire");

        var entry = Assert.Single(_audit);
        Assert.Equal("tenant.address_changed", entry.Action);
        Assert.Equal(_operator, entry.PlatformUserId);
        Assert.Equal(_tenant.PublicId, entry.TenantPublicId);
        Assert.Equal("acme -> acme-fire", entry.Detail);
    }

    [Fact]
    public async Task Refuses_an_address_another_tenant_has_or_once_had()
    {
        _tenants.Setup(t => t.FindAddressAsync("beta", default)).ReturnsAsync(
            new TenantSubdomain { Subdomain = "beta", TenantId = 9, ReplacedAt = Now.AddDays(-1) });

        var result = await Change("beta");

        Assert.Equal(PlatformProblems.SubdomainTaken, result.ProblemCode);
        Assert.Null(_current.ReplacedAt);
        _tenants.Verify(t => t.BeginTransactionAsync(default), Times.Never);
    }

    [Fact]
    public async Task Takes_back_one_of_its_own_former_addresses()
    {
        var former = new TenantSubdomain { Subdomain = "acme-old", TenantId = 7, AssignedAt = Now.AddDays(-90), ReplacedAt = Now.AddDays(-30) };
        _tenants.Setup(t => t.FindAddressAsync("acme-old", default)).ReturnsAsync(former);

        var result = await Change("acme-old");

        Assert.True(result.Succeeded);
        Assert.Null(former.ReplacedAt);
        Assert.Equal(Now, former.AssignedAt);
        Assert.Equal(Now, _current.ReplacedAt);
        Assert.Empty(_added);
        // The reclaimed row's own dates are overwritten; its earlier span was recorded when it was left.
        Assert.Equal("acme", Assert.Single(_periods).Subdomain);
    }

    [Fact]
    public async Task Says_so_when_the_tenant_is_already_at_that_address()
    {
        var result = await Change("ACME");

        Assert.Equal(PlatformProblems.AlreadyInThatState, result.ProblemCode);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task A_race_lost_at_the_database_is_refused_and_rolled_back()
    {
        _tenants.Setup(t => t.TrySaveAsync(default)).ReturnsAsync(false);

        var result = await Change("acme-fire");

        Assert.Equal(PlatformProblems.SubdomainTaken, result.ProblemCode);
        _transaction.Verify(t => t.RollbackAsync(default), Times.Once);
        _transaction.Verify(t => t.CommitAsync(default), Times.Never);
    }

    [Fact]
    public async Task A_retired_tenant_keeps_its_address()
    {
        _tenant.Status = TenantStatus.Retired;

        var result = await Change("acme-fire");

        Assert.Equal(PlatformProblems.TenantRetired, result.ProblemCode);
        _tenants.Verify(t => t.BeginTransactionAsync(default), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a")]
    [InlineData("support")]
    public async Task An_unusable_address_is_refused_before_any_lookup(string? subdomain)
    {
        var result = await Change(subdomain);

        Assert.Equal(PlatformProblems.ValidationFailed, result.ProblemCode);
        _tenants.Verify(t => t.FindByPublicIdAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Theory]
    [InlineData("emp_7h2k3m4n5p6q7r8s9t0v1")]
    [InlineData("nonsense")]
    public async Task Something_that_is_not_a_tenant_id_is_not_found(string id)
    {
        var result = await new ChangeTenantAddressCommandHandler(_tenants.Object, new FakeTimeProvider(Now))
            .Handle(_operator, id, new("acme-fire"));

        Assert.Equal(PlatformProblems.NotFound, result.ProblemCode);
    }

    [Fact]
    public async Task An_unknown_tenant_is_not_found()
    {
        var result = await new ChangeTenantAddressCommandHandler(_tenants.Object, new FakeTimeProvider(Now))
            .Handle(_operator, PublicId.New("ten").ToString(), new("acme-fire"));

        Assert.Equal(CommandOutcome.NotFound, result.Outcome);
    }
}
