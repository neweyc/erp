namespace AppPlatform.Platform.Services;

public record ProvisionRequest(string TenantName, string AdminEmail, string IdempotencyKey, string Subdomain);

/// <param name="ProblemCode">Core's code when it refused, so a refusal the operator can act on
/// (the address is taken) is told apart from a failure worth retrying.</param>
public record ProvisionResult(bool Succeeded, string? TenantPublicId, string? Error, string? ProblemCode = null);

/// <summary>
/// Calls core's internal provisioning endpoint.
///
/// Platform does NOT write tenant business data: the tenant row, its company, and the admin
/// invite must commit in one transaction, and a transaction cannot span two services — so core
/// does all of it, and platform asks. An interface because the boundary between the two
/// processes is exactly what a unit test needs to stand in for.
/// </summary>
public interface IProvisioningClient
{
    Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default);
}
