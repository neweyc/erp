using AppPlatform.Audit;

namespace AppPlatform.Core.Data;

/// <summary>
/// A part of the organization employees are placed in, such as Sales or Engineering › Platform.
/// Departments nest: each may have a parent, to a depth of <see cref="MaxDepth"/>.
///
/// A catalog like <see cref="JobTitle"/> and <see cref="Location"/>: named, deactivated rather than
/// deleted, written only under the tenant's catalog lock (ICatalogService.LockCatalogsAsync). The
/// rules for the tree (no loops, no active department under an inactive one) are in DepartmentRules.
///
/// Employees hold it by id and no copy of the name is stored anywhere in core, so a rename shows
/// everywhere at once. An app keeps its own display snapshot if it needs the name as it was.
/// </summary>
public class Department : IAuditable
{
    public const int NameMaxLength = 100;

    /// <summary>
    /// Levels from the top, counting the top as 1. A limit so the tree stays something a person
    /// can read in a picker; well beyond any organization chart we expect.
    /// </summary>
    public const int MaxDepth = 8;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public int CompanyId { get; set; }
    public string PublicId { get; set; } = "";

    /// <summary>Unique among its siblings, ignoring case: "Support" may sit under both Sales and Engineering.</summary>
    public required string Name { get; set; }

    /// <summary>Null for a top-level department.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>
    /// False when retired. Employees already in it keep it; it is no longer offered for new
    /// placements. Never deleted, so history and apps' references still resolve.
    /// </summary>
    public bool Active { get; set; } = true;

    /// <summary>Advanced by every edit; it numbers the department's events (created is 1).</summary>
    public long Version { get; set; } = 1;
}
