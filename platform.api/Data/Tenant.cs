using AppPlatform.Ids;
using AppPlatform.Tenancy;

namespace AppPlatform.Platform.Data;

public enum TenantStatus
{
    Active,
    /// <summary>Reversible. The customer still owns their data and keeps auth + export.</summary>
    Suspended,
    /// <summary>Terminal. Sessions are revoked; there is no allowlist to maintain.</summary>
    Retired,
}

/// <summary>
/// The customer. Platform OWNS this table's DDL and its lifecycle; core holds SELECT and
/// INSERT so provisioning can commit the tenant, its company, and the admin invite in one
/// transaction. Core has no UPDATE: it can bring a tenant into existence, and only the
/// operator can change what it is permitted to do.
/// </summary>
public class Tenant : IPublicIdentified
{
    public int Id { get; set; }
    public string PublicId { get; set; } = "";
    public required string Name { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;

    /// <summary>Null means no idle window. Never read as zero — that would sign everyone out.</summary>
    public int? IdleTimeoutMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }

    /// <summary>
    /// The caller-supplied key that created this tenant, under a unique index.
    ///
    /// Idempotency lives HERE, on the row itself, rather than in a record written after the
    /// fact. Recording the key once provisioning returns cannot prevent a duplicate: two
    /// concurrent calls both create a tenant, and only one of them then wins the race to
    /// record — leaving a second tenant nobody asked for and nothing to roll it back. With the
    /// key on the row, the database refuses the second INSERT inside the same transaction that
    /// would have created it.
    /// </summary>
    public string? ProvisioningKey { get; set; }
}

/// <summary>
/// An address a tenant signs in at (<c>acme-fire</c>, meaning <c>acme-fire.{base domain}</c>), and
/// how sign-in finds the tenant. One row per address, kept after a rename so the address stays the
/// tenant's; when each address was in use is recorded in <see cref="TenantAddressPeriod"/>.
///
/// The subdomain is the key, so an address belongs to one tenant forever and is never given to
/// another. Its people may still be signed in at the old address after a rename, holding a cookie
/// for that host; another tenant taking it would put them on a stranger's address. A tenant may
/// take back one of its own former addresses.
///
/// Core holds SELECT and INSERT here, as on <see cref="Tenant"/>: provisioning writes the first
/// address in the same transaction as the tenant, and sign-in reads it. Only the operator renames.
/// </summary>
public class TenantSubdomain
{
    public required string Subdomain { get; set; }
    public int TenantId { get; set; }

    /// <summary>When it last became the tenant's current address; a tenant taking it back resets it.</summary>
    public DateTimeOffset AssignedAt { get; set; }

    /// <summary>Null for the tenant's current address; set when a rename replaces it.</summary>
    public DateTimeOffset? ReplacedAt { get; set; }
}

/// <summary>
/// A span of time a tenant signed in at an address it has since left, written by the rename that
/// left it. <see cref="TenantSubdomain"/> holds one row per address, so a tenant that moves away
/// and later takes an address back overwrites that row's dates; these rows keep each earlier span.
/// The current address has no row here until it too is replaced. Platform only: core never reads
/// it. Append-only, because it is history.
/// </summary>
public class TenantAddressPeriod : IAppendOnly
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public required string Subdomain { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset ReplacedAt { get; set; }
}

/// <summary>
/// An entitlement: the tenant has licensed this app. **Rows exist only for licensed apps** —
/// core has no row, because core is always on. Not tenant-scoped: entitlements are managed at
/// host level, by an operator, and a tenant must not be able to grant itself one.
/// </summary>
public class TenantApp
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public required string App { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null;
}
