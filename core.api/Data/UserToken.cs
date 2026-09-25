using AppPlatform.Tenancy;

namespace AppPlatform.Core.Data;

public enum TokenPurpose
{
    Invite,
    PasswordReset,
}

/// <summary>
/// A single-use token sent by email.
///
/// Stored HASHED, never in plaintext: the database is the one place these must not be readable,
/// because a token is password-equivalent until it is used. Whoever holds the row would
/// otherwise be able to take over any invited account.
/// </summary>
public class UserToken : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public int TenantId { get; set; }
    public Guid UserId { get; set; }
    public TokenPurpose Purpose { get; set; }

    /// <summary>SHA-256 of the token. Compared by hash, so the plaintext exists only in the email.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set when consumed, and also the concurrency token: two simultaneous uses of one link must
    /// not both succeed, and checking "is it null" before updating has a race between the two
    /// statements.
    /// </summary>
    public DateTimeOffset? UsedAt { get; set; }
}
