using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace AppPlatform.Platform.Services;

/// <summary>A tenant as the console lists it: commercial state only, never business data.</summary>
/// <param name="Subdomain">The current address; null only if a tenant somehow has none.</param>
public record TenantSummary(
    string TenantId, string Name, string Status, DateTimeOffset CreatedAt, DateTimeOffset? StatusChangedAt,
    List<string> Apps, string? Subdomain)
{
    /// <summary>The full address, filled in by the handler from <see cref="ITenantSite"/>.</summary>
    public string? SignInUrl { get; init; }
}

/// <summary>An address the tenant has had: the current one has no <see cref="ReplacedAt"/>.</summary>
public record AddressRow(string Subdomain, DateTimeOffset AssignedAt, DateTimeOffset? ReplacedAt);

/// <summary>One grant of an app, live or revoked: the commercial record of what a tenant had and when.</summary>
public record GrantRow(string App, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt);

public interface ITenantService
{
    /// <summary>Every tenant, by name, with its live apps, in one query rather than one per tenant.</summary>
    Task<List<TenantSummary>> ListSummariesAsync(CancellationToken ct = default);

    /// <summary>Every grant the tenant has ever had, newest first, revoked ones included.</summary>
    Task<List<GrantRow>> GrantHistoryAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// The current address, then each span at a former one, latest first. An address the tenant
    /// left, took back and left again appears once per span.
    /// </summary>
    Task<List<AddressRow>> AddressHistoryAsync(int tenantId, CancellationToken ct = default);

    /// <summary>The tenant's current address, tracked for a rename.</summary>
    Task<TenantSubdomain?> CurrentAddressAsync(int tenantId, CancellationToken ct = default);

    /// <summary>Whoever holds or once held this address, tracked; null when it was never used.</summary>
    Task<TenantSubdomain?> FindAddressAsync(string subdomain, CancellationToken ct = default);

    Task<Tenant?> FindByPublicIdAsync(string publicId, CancellationToken ct = default);
    Task<TenantApp?> FindGrantAsync(int tenantId, string app, CancellationToken ct = default);
    void Add(TenantApp grant);
    void Add(TenantSubdomain address);
    void Add(TenantAddressPeriod period);
    void Audit(PlatformAuditLog entry);
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves, or returns false when a unique index refused it: a race another request won. What
    /// was staged is discarded, and the open transaction must be rolled back.
    /// </summary>
    Task<bool> TrySaveAsync(CancellationToken ct = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);
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
