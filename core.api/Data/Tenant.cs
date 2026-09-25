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
}
