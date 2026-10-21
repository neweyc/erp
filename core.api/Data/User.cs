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
    /// <summary>
    /// The keys of the roles this user holds: built-in (<c>ledger:controller</c>) or custom (a role's
    /// <c>rol_…</c> public id). A column rather than a table so a change of roles is an ordinary
    /// audited update of this row. Written by ChangeUserRolesFeature (and set once at invitation and
    /// provisioning). Replace the array rather than mutating it, so the change is plainly a new
    /// value. See docs/permissions.md §3.
    /// </summary>
    public string[] Roles { get; set; } = [];

    /// <summary>
    /// Advanced by every change to the user's access (roles, deactivation, reactivation); it numbers
    /// the <c>user.roles_changed</c>, <c>user.deactivated</c> and <c>user.reactivated</c> events.
    ///
    /// Deliberately NOT a concurrency token. EF puts a token in the WHERE clause of EVERY update to
    /// the row, so a password rehash at sign-in while an administrator changed the user's roles
    /// would match no row and fail. Access changes are serialized instead by a per-tenant advisory
    /// lock, taken before anything is read (ChangeUserRolesFeature), which leaves other writes to
    /// the user unaffected.
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
