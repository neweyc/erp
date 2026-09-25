using AppPlatform.Tenancy;

namespace AppPlatform.Core.Data;

/// <summary>
/// A signed-in session. Every auth cookie carries one of these row ids, and it is revalidated on
/// every request — which is what makes revocation, deactivation, role change, suspension and
/// entitlement change take effect immediately rather than at cookie expiry.
/// </summary>
public class Session : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Whether this session has cleared the MFA gate. False until a challenge is passed.</summary>
    public bool MfaSatisfied { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset AbsoluteExpiry { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
