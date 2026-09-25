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
    public required string Role { get; set; }

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
