namespace AppPlatform.Platform.Services;

public record ProvisionRequest(string TenantName, string AdminEmail, string IdempotencyKey, string Subdomain);

/// <param name="Refusal">Set when core refused the request itself (400 or 409), as opposed to
/// failing: something the operator can act on, such as an address that is taken.</param>
public record ProvisionResult(bool Succeeded, string? TenantPublicId, string? Error, ProvisionRefusal? Refusal = null);

/// <summary>
/// Core's refusal, passed to the operator as core gave it: its stable code to branch on, and its
/// message to read. Passing every refusal through means a rule core adds (or applies differently
/// during version skew) reaches the operator as itself, not as "try again", which would fail forever.
/// </summary>
/// <param name="IsConflict">409, the request clashed with existing data; otherwise 400, invalid.</param>
public record ProvisionRefusal(bool IsConflict, string ProblemCode, string Message);

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
