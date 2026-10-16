using AppPlatform.Audit;

namespace AppPlatform.Core.Data;

/// <summary>
/// A role one tenant defined: a named bundle of permissions from any apps. Lives in the
/// <c>identity</c> schema. Built-in roles are code, not rows (<c>BuiltInRoles</c> in packages/auth).
/// Design: docs/permissions.md.
///
/// A user holds it by its public id, in <see cref="User.Roles"/>. Written only by the role
/// features, under the tenant's role-change lock.
/// </summary>
public class Role : IAuditable
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public string PublicId { get; set; } = "";

    /// <summary>Unique in the tenant ignoring case, and never a built-in role's name.</summary>
    public required string Name { get; set; }
    public string Description { get; set; } = "";

    /// <summary>
    /// Permission names from the catalogue. Replace the array rather than mutating it, so the
    /// change is plainly a new value to EF and to the audit row.
    /// </summary>
    public string[] Permissions { get; set; } = [];

    /// <summary>Advanced by every edit; it numbers the role's events (created is 1).</summary>
    public long Version { get; set; } = 1;
}
