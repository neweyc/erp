using AppPlatform.Audit;

namespace AppPlatform.Core.Data;

/// <summary>
/// What someone's job is called: "Engineer", "Shift supervisor". Shared by the whole tenant, so it
/// carries no company. A catalog like <see cref="Department"/>: deactivated rather than deleted,
/// held by id, and written only under the tenant's catalog lock.
/// </summary>
public class JobTitle : IAuditable
{
    public const int NameMaxLength = 100;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public string PublicId { get; set; } = "";

    /// <summary>Unique in the tenant ignoring case.</summary>
    public required string Name { get; set; }

    /// <summary>False when retired: kept by those who have it, no longer offered.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Advanced by every edit; it numbers the job title's events (created is 1).</summary>
    public long Version { get; set; } = 1;
}
