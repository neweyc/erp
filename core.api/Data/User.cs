using AppPlatform.Audit;
using AppPlatform.Ids;
using AppPlatform.Tenancy;

namespace AppPlatform.Core.Data;

public enum UserStatus
{
    Invited,
    Active,
    Deactivated,
}

/// <summary>
/// An account that can sign in. Lives in the <c>identity</c> schema.
/// </summary>
public class User : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public int CompanyId { get; set; }
    public string PublicId { get; set; } = "";

    public required string Email { get; set; }
    /// <summary>The core role. Its permissions are in <c>RoleCatalogue</c> (packages/auth).</summary>
    public required string Role { get; set; }

    /// <summary>
    /// One <c>app:role</c> entry per app, e.g. <c>tickets:agent</c>. A column rather than a table so
    /// a role change is an ordinary audited update of this row. Written only by
    /// ChangeUserRolesFeature, which keeps it to one entry per app. Replace the array rather than
    /// mutating it, so the change is plainly a new value. See docs/permissions.md §3.
    /// </summary>
    public string[] AppRoles { get; set; } = [];

    /// <summary>
    /// Advanced by every role change; it numbers the <c>user.roles_changed</c> events.
    ///
    /// Deliberately NOT a concurrency token. EF puts a token in the WHERE clause of EVERY update to
    /// the row, so an invitation accepted while an administrator changed the user's roles would
    /// match no row and fail. Role changes are serialized instead by a per-tenant advisory lock,
    /// taken before anything is read (ChangeUserRolesFeature), which leaves other writes to the
    /// user unaffected.
    /// </summary>
    public long AccessVersion { get; set; } = 1;

    /// <summary>
    /// Null until the invitation is accepted. An invited account has no password and cannot
    /// sign in — which is what makes an invitation mean something rather than being a
    /// pre-activated account with a link attached.
    /// </summary>
    /// <summary>Redacted in the audit log: a hash is still a secret worth not copying.</summary>
    [AuditRedacted]
    public string? PasswordHash { get; set; }
    public UserStatus Status { get; set; } = UserStatus.Invited;

    /// <summary>
    /// The link that makes termination cut access: terminating an employee deactivates the
    /// LINKED account and revokes its sessions, so an unlinked account would survive its
    /// employee's departure. It also gates every self-service surface.
    /// </summary>
    public Guid? EmployeeId { get; set; }

    public bool Active => Status == UserStatus.Active;
}
