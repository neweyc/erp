using AppPlatform.Core.Data;

namespace AppPlatform.Core.Services;

/// <summary>
/// The one way an invitation token is issued, used by both paths that invite someone: a tenant's
/// first admin at provisioning, and an employee invited by an admin. Kept in one place because
/// its rules are security rules — how long a link stays usable and what is stored — and two
/// copies would drift.
///
/// The caller adds the returned row to its context and puts the plaintext in the staged email,
/// committing both in ONE save with the invited account. The row holds only a hash; the plaintext
/// exists in the email and in that outbox payload until the outbox prunes it.
/// </summary>
public static class InvitationToken
{
    /// <summary>
    /// Long enough to survive a weekend and a spam folder; short enough that a forgotten
    /// invitation is not a permanent way in.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static (UserToken Row, string Plaintext) Issue(Guid userId, DateTimeOffset now)
    {
        var (plaintext, hash) = TokenGenerator.Create();

        var row = new UserToken
        {
            UserId = userId,
            Purpose = TokenPurpose.Invite,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = now.Add(Lifetime),
        };

        return (row, plaintext);
    }
}
