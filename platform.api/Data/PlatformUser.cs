using AppPlatform.Ids;

namespace AppPlatform.Platform.Data;

/// <summary>
/// An operator. Deliberately a SEPARATE identity from tenant users: separate table, separate
/// session table, separate cookie, separate hostname. A console session must never authenticate
/// a tenant API, or the reverse.
///
/// MFA is mandatory for operators — an operator reaches the control plane for every tenant, so
/// an unprotected operator account is a larger exposure than most of what M2 gates. Sign-in
/// requires a current authenticator code; see <see cref="Auth.OperatorMfa"/>.
/// </summary>
public class PlatformUser : IPublicIdentified
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string PublicId { get; set; } = "";
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>
    /// The authenticator secret, ENCRYPTED (<c>enc:v1:…</c>) under the platform's own data key —
    /// never the tenant key-encryption key, which this process does not receive. Read and written
    /// only through <see cref="Auth.OperatorMfa"/>. Null until the operator is enrolled.
    /// </summary>
    public string? TotpSecretEncrypted { get; set; }

    /// <summary>
    /// A concurrency token that works on an encrypted column. AES-GCM re-encrypts fresh every
    /// time, so the ciphertext differs on every write and cannot itself be compared.
    /// </summary>
    public int TotpSecretVersion { get; set; }

    /// <summary>
    /// The 30-second step of the last code accepted. A code is accepted once: only a later step
    /// passes (RFC 6238 §5.2). Advanced atomically with the session it admits; see IOperatorSessionStore.CreateSessionWithCodeAsync.
    /// </summary>
    public long? TotpLastUsedStep { get; set; }

    public bool MfaEnabled => TotpSecretEncrypted is not null;
    public DateTimeOffset CreatedAt { get; set; }
}

public class PlatformSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PlatformUserId { get; set; }

    /// <summary>
    /// Whether this session has cleared the MFA gate. Always true today: sign-in takes the
    /// password and the authenticator code together and creates no session until both pass, so a
    /// half-authenticated session never exists. The evaluator still refuses one without it, as a
    /// second line should a later change create sessions another way.
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

/// <summary>
/// One row of the operator error feed: metadata about an Error-level occurrence in any service,
/// and deliberately nothing else (packages/errorfeed's ErrorOccurrence says why there is no message
/// or stack trace). Written only through platform_v1.record_error, which checks every value's shape;
/// read only by operators. Pruned after <see cref="Services.ErrorFeedPruner.Retention"/>.
/// </summary>
public class ErrorFeedEntry
{
    public long Id { get; set; }
    public required string Reference { get; set; }
    public required string Fingerprint { get; set; }
    public required string App { get; set; }
    public int? TenantId { get; set; }
    public int Count { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
