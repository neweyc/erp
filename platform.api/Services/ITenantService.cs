using AppPlatform.Platform.Data;

namespace AppPlatform.Platform.Services;

public interface ITenantService
{
    Task<Tenant?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);
    Task<List<Tenant>> ListAsync(CancellationToken ct = default);
    Task<List<string>> LicensedAppsAsync(int tenantId, CancellationToken ct = default);
    Task<TenantApp?> FindGrantAsync(int tenantId, string app, CancellationToken ct = default);
    void Add(TenantApp grant);
    void Audit(PlatformAuditLog entry);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IIdempotencyService
{
    Task<string?> FindResponseAsync(string operation, string key, CancellationToken ct = default);

    /// <summary>
    /// Records the outcome. Returns false when the key was already taken — a race, not an
    /// error: the other caller won, and its response is the one to return.
    /// </summary>
    Task<bool> TryRecordAsync(string operation, string key, string responseJson, CancellationToken ct = default);
}
