using AppPlatform.Api;
using AppPlatform.Platform.Features.Tenants;
using AppPlatform.Platform.Services;
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
        _idempotency.Setup(i => i.TryRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default))
            .ReturnsAsync(true);
    }

    private CreateTenantCommandHandler Handler() => new(_provisioning.Object, _idempotency.Object);

    [Fact]
    public async Task A_valid_request_delegates_to_core_and_records_the_outcome()
    {
        var result = await Handler().Handle(new("Acme", "Admin@Acme.com", "key-1"));

        Assert.True(result.Succeeded);
        // Platform never writes tenant business data — it asks core, which can commit the
        // tenant, its company, and the admin invite in one transaction.
        _provisioning.Verify(p => p.ProvisionAsync(
            It.Is<ProvisionRequest>(r => r.TenantName == "Acme" && r.AdminEmail == "admin@acme.com"), default),
            Times.Once);
        _idempotency.Verify(i => i.TryRecordAsync(Operation, "key-1", It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_refused()
    {
        var result = await Handler().Handle(new("Acme", "a@b.c", null));

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

        var result = await Handler().Handle(new("Acme", "a@b.c", "key-1"));

        Assert.True(result.Succeeded);
        // The failure this prevents: a timed-out call retried, provisioning a SECOND tenant
        // that the caller never learns about and the operator finds as a duplicate customer.
        _provisioning.Verify(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default), Times.Never);
        Assert.Contains("ten_original", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Losing_the_race_returns_the_winners_result_not_ours()
    {
        _idempotency.Setup(i => i.TryRecordAsync(Operation, "key-1", It.IsAny<string>(), default))
            .ReturnsAsync(false);
        _idempotency.SetupSequence(i => i.FindResponseAsync(Operation, "key-1", default))
            .ReturnsAsync((string?)null)
            .ReturnsAsync("""{"tenantId":"ten_winner"}""");

        var result = await Handler().Handle(new("Acme", "a@b.c", "key-1"));

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

        var result = await Handler().Handle(new("Acme", "a@b.c", "key-1"));

        Assert.Equal(PlatformProblems.ProvisioningFailed, result.ProblemCode);
        // Recording the failure would make every retry replay it forever, when a retry after a
        // transient fault is exactly what should be allowed to work.
        _idempotency.Verify(i => i.TryRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Theory]
    [InlineData(null, "a@b.c")]
    [InlineData("Acme", null)]
    [InlineData("   ", "a@b.c")]
    public async Task Missing_details_are_refused_before_calling_core(string? name, string? email)
    {
        var result = await Handler().Handle(new(name, email, "key-1"));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        _provisioning.Verify(p => p.ProvisionAsync(It.IsAny<ProvisionRequest>(), default), Times.Never);
    }
}
