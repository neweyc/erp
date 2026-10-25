using AppPlatform.Api;
using AppPlatform.Platform.Data;
using AppPlatform.Platform.Features.Tenants;
using AppPlatform.Platform.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;
using static AppPlatform.Platform.Features.Tenants.CreateTenantFeature;

namespace AppPlatform.Platform.Tests;

public class CreateTenantTests
{
    private readonly Mock<IProvisioningClient> _provisioning = new();
    private readonly Mock<IIdempotencyService> _idempotency = new();

    public CreateTenantTests()
    {
        _provisioning.Setup(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default))
            .ReturnsAsync(new ProvisionResult(true, "ten_7h2k3m4n5p6q7r8s9t0v1", null));
        _idempotency.Setup(i => i.TryRecordAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlatformAuditLog>(), default))
            .Callback<string, string, string, PlatformAuditLog, CancellationToken>((_, _, _, audit, _) => _audit.Add(audit))
            .ReturnsAsync(true);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _operator = Guid.NewGuid();
    private readonly List<PlatformAuditLog> _audit = [];

    private CreateTenantCommandHandler Handler() => new(_provisioning.Object, _idempotency.Object, new FakeTimeProvider(Now));

    [Fact]
    public async Task A_valid_request_delegates_to_core_and_records_the_outcome()
    {
        var result = await Handler().Handle(_operator, new("Acme", "Admin@Acme.com", "key-1", "acme"));

        Assert.True(result.Succeeded);
        // Platform never writes tenant business data — it asks core, which can commit the
        // tenant, its company, and the admin invite in one transaction.
        _provisioning.Verify(p => p.ProvisionAsync(
            It.Is<ProvisionRequest>(r =>
                r.TenantName == "Acme" && r.AdminEmail == "admin@acme.com" && r.Subdomain == "acme"), default),
            Times.Once);
        _idempotency.Verify(i => i.TryRecordAsync(
            Operation, "key-1", It.IsAny<string>(), It.IsAny<PlatformAuditLog>(), default), Times.Once);
    }

    [Fact]
    public async Task Creating_a_tenant_is_audited_with_the_operator_and_without_the_admins_address()
    {
        await Handler().Handle(_operator, new("Acme", "admin@acme.com", "key-1", "acme"));

        // Committed with the idempotency record, so it is written once however often the call is retried.
        var entry = Assert.Single(_audit);
        Assert.Equal("tenant.created", entry.Action);
        Assert.Equal(_operator, entry.PlatformUserId);
        Assert.Equal("ten_7h2k3m4n5p6q7r8s9t0v1", entry.TenantPublicId);
        Assert.Equal(Now, entry.CreatedAt);
        // The admin's address is customer data; the operator log names the tenant only.
        Assert.Equal("Acme", entry.Detail);
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_refused()
    {
        var result = await Handler().Handle(_operator, new("Acme", "a@b.c", null, "acme"));

        // Required rather than generated: a caller that did not supply one cannot correlate a
        // retry with its original attempt, so generating one here would defeat the point.
        Assert.Equal(PlatformProblems.IdempotencyKeyRequired, result.ProblemCode);
        _provisioning.Verify(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task A_replayed_key_returns_the_original_result_without_provisioning_again()
    {
        _idempotency.Setup(i => i.FindResponseAsync(Operation, "key-1", default))
            .ReturnsAsync("""{"tenantId":"ten_original"}""");

        var result = await Handler().Handle(_operator, new("Acme", "a@b.c", "key-1", "acme"));

        Assert.True(result.Succeeded);
        // The failure this prevents: a timed-out call retried, provisioning a SECOND tenant
        // that the caller never learns about and the operator finds as a duplicate customer.
        _provisioning.Verify(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default), Times.Never);
        // And no second audit entry: the tenant was created once.
        Assert.Empty(_audit);
        Assert.Contains("ten_original", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Losing_the_race_returns_the_winners_result_not_ours()
    {
        _idempotency.Setup(i => i.TryRecordAsync(Operation, "key-1", It.IsAny<string>(), It.IsAny<PlatformAuditLog>(), default))
            .ReturnsAsync(false);
        _idempotency.SetupSequence(i => i.FindResponseAsync(Operation, "key-1", default))
            .ReturnsAsync((string?)null)
            .ReturnsAsync("""{"tenantId":"ten_winner"}""");

        var result = await Handler().Handle(_operator, new("Acme", "a@b.c", "key-1", "acme"));

        // Two concurrent retries both find nothing and both provision; only the unique index
        // stops both being recorded. Returning our own id would hand out a different tenant for
        // the same logical operation.
        Assert.Contains("ten_winner", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provisioning_failure_is_not_recorded_so_a_retry_can_still_succeed()
    {
        _provisioning.Setup(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default))
            .ReturnsAsync(new ProvisionResult(false, null, "core returned 500"));

        var result = await Handler().Handle(_operator, new("Acme", "a@b.c", "key-1", "acme"));

        Assert.Equal(PlatformProblems.ProvisioningFailed, result.ProblemCode);
        // Recording the failure would make every retry replay it forever, when a retry after a
        // transient fault is exactly what should be allowed to work.
        _idempotency.Verify(i => i.TryRecordAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlatformAuditLog>(), default), Times.Never);
    }

    [Theory]
    [InlineData(null, "a@b.c")]
    [InlineData("Acme", null)]
    [InlineData("   ", "a@b.c")]
    public async Task Missing_details_are_refused_before_calling_core(string? name, string? email)
    {
        var result = await Handler().Handle(_operator, new(name, email, "key-1", "acme"));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        _provisioning.Verify(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("www")]
    public async Task An_unusable_address_is_refused_before_calling_core(string? subdomain)
    {
        var result = await Handler().Handle(_operator, new("Acme", "a@b.c", "key-1", subdomain));

        Assert.Equal(PlatformProblems.ValidationFailed, result.ProblemCode);
        _provisioning.Verify(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task The_address_is_sent_as_stored_lowercase()
    {
        await Handler().Handle(_operator, new("Acme", "a@b.c", "key-1", " Acme-Fire "));

        _provisioning.Verify(p => p.ProvisionAsync(It.Is<ProvisionRequest>(r => r.Subdomain == "acme-fire"), default));
    }

    [Fact]
    public async Task An_address_core_says_is_taken_is_a_conflict_the_operator_can_fix_and_retry()
    {
        _provisioning.Setup(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default))
            .ReturnsAsync(new ProvisionResult(false, null, "core returned 409", PlatformProblems.SubdomainTaken));

        var result = await Handler().Handle(_operator, new("Acme", "a@b.c", "key-1", "acme"));

        Assert.Equal(CommandOutcome.Conflict, result.Outcome);
        Assert.Equal(PlatformProblems.SubdomainTaken, result.ProblemCode);
        // Not recorded, so the same key with another address goes on to create the tenant.
        _idempotency.Verify(i => i.TryRecordAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlatformAuditLog>(), default), Times.Never);
    }
}
