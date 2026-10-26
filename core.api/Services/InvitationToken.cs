using System.Text.Json;
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
///
/// The email payloads are built here too, so the three paths that send one cannot drift. Set the
/// token's <see cref="UserToken.MessageId"/> to the staged message's id: it is how the Users page
/// reports that email's delivery.
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

    /// <summary>The email inviting someone into an organization that already exists.</summary>
    public static string EmailPayload(string userPublicId, string plaintext, string signInUrl)
        => Payload("invite", userPublicId, plaintext, signInUrl);

    /// <summary>The email inviting a new organization's first administrator.</summary>
    public static string TenantAdminEmailPayload(string userPublicId, string plaintext, string signInUrl)
        => Payload("tenant_admin_invite", userPublicId, plaintext, signInUrl);

    /// <summary>
    /// What the email transport needs to write the message. <c>acceptUrl</c> is the link, on the
    /// tenant's address as it is when the invitation is made: the shell there sets the password and
    /// leaves the person on their sign-in page. If the address changes before they use it, the old
    /// link still works, because acceptance finds the tenant from the token, and acceptance answers
    /// with the current address so the shell sends them on (AcceptInviteFeature).
    /// </summary>
    private static string Payload(string kind, string userPublicId, string plaintext, string signInUrl)
        => JsonSerializer.Serialize(new
        {
            kind,
            userId = userPublicId,
            token = plaintext,
            acceptUrl = $"{signInUrl}/accept-invite?token={Uri.EscapeDataString(plaintext)}",
        });
}
