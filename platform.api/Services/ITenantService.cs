using AppPlatform.Platform.Data;

namespace AppPlatform.Platform.Services;

/// <summary>A tenant as the console lists it: commercial state only, never business data.</summary>
public record TenantSummary(
    string TenantId, string Name, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StatusChangedAt,
    List<string> Apps);

/// <summary>One grant of an app, live or revoked: the commercial record of what a tenant had and when.</summary>
public record GrantRow(string App, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt);

public interface ITenantService
{
    /// <summary>Every tenant, by name, with its live apps, in one query rather than one per tenant.</summary>
    Task<List<TenantSummary>> ListSummariesAsync(CancellationToken ct = default);

    /// <summary>Every grant the tenant has ever had, newest first, revoked ones included.</summary>
    Task<List<GrantRow>> GrantHistoryAsync(int tenantId, CancellationToken ct = default);

    Task<Tenant?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);
    Task<TenantApp?> FindGrantAsync(int tenantId, string app, CancellationToken ct = default);
    void Add(TenantApp grant);
    void Audit(PlatformAuditLog entry);
    Task SaveAsync(CancellationToken ct = default);
}

public interface IIdempotencyService
{
    Task<string?> FindResponseAsync(string operation, string key, CancellationToken ct = default);

    /// <summary>
    /// Records the outcome, and the operator audit entry for it, in one save. Returns false when
    /// the key was already taken — a race, not an error: the other caller won, its response is the
    /// one to return, and its audit entry is the one that stands, so ours is discarded with it.
    /// </summary>
    Task<bool> TryRecordAsync(
        string operation, string key, string responseJson, PlatformAuditLog audit, CancellationToken ct = default);
}
