using AppPlatform.Ids;

namespace AppPlatform.Core.Data;

public enum TenantLifecycle
{
    Active,
    Suspended,
    Retired,
}

/// <summary>
/// The customer. Lives in the <c>platform</c> schema, which core reaches for one reason only:
/// the tenant row, its company, and the admin invite must commit in a single transaction, and
/// a transaction cannot span two services.
///
/// Core holds SELECT and INSERT on this table and nothing else in that schema — no UPDATE.
/// Core can bring a tenant into existence; only the operator can change what it is permitted
/// to do. Deliberately NOT ITenantScoped: it is the thing tenants are scoped BY.
/// </summary>
public class Tenant : IPublicIdentified
{
    public int Id { get; set; }
    public string PublicId { get; set; } = "";
    public required string Name { get; set; }
    public TenantLifecycle Status { get; set; } = TenantLifecycle.Active;
    public int? IdleTimeoutMinutes { get; set; }

    /// <summary>
    /// Not-null in the platform schema with no database default, so core MUST supply it. A twin
    /// entity that omits a required column compiles, passes every unit test, and fails on the
    /// first real INSERT — which is why TwinParityTests compares the two models directly.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StatusChangedAt { get; set; }

    /// <summary>Written by provisioning; unique, and what makes a retry safe. See the platform twin.</summary>
    public string? ProvisioningKey { get; set; }
}
