using AppPlatform.Ids;

namespace AppPlatform.Platform.Data;

/// <summary>
/// An operator. Deliberately a SEPARATE identity from tenant users: separate table, separate
/// session table, separate cookie, separate hostname. A console session must never authenticate
/// a tenant API, or the reverse.
///
/// MFA is mandatory for operators — an operator reaches the control plane for every tenant, so
/// an unprotected operator account is a larger exposure than most of what M2 gates. Enforcement
/// lands in M2; the column exists now so enrolment is not a schema change later.
/// </summary>
public class PlatformUser : IPublicIdentified
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string PublicId { get; set; } = "";
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>
    /// Encrypted at rest with the PLATFORM's own key — never the tenant field key, which this
    /// process does not receive and could not use.
    /// </summary>
    public string? TotpSecret { get; set; }

    /// <summary>
    /// A concurrency token that works on an encrypted column. AES-GCM re-encrypts fresh every
    /// time, so the ciphertext differs on every write and cannot itself be compared.
    /// </summary>
    public int TotpSecretVersion { get; set; }

    public bool MfaEnabled => TotpSecret is not null;
    public DateTimeOffset CreatedAt { get; set; }
}

public class PlatformSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PlatformUserId { get; set; }

    /// <summary>
    /// Whether this session has cleared the MFA gate.
    ///
    /// A real column, not a computed placeholder. Today sign-in sets it true because no
    /// challenge exists yet; in M2 sign-in sets it FALSE and the challenge sets it true, and
    /// nothing else has to change — the evaluator already refuses a session without it.
    /// </summary>
    public bool MfaSatisfied { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset AbsoluteExpiry { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Operator actions, separate from tenant audit by design: mixing them would make a tenant's
/// own audit trail include entries for things nobody in that tenant did.
/// </summary>
public class PlatformAuditLog
{
    public long Id { get; set; }
    public Guid? PlatformUserId { get; set; }
    public required string Action { get; set; }
    public string? TenantPublicId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
