using AppPlatform.Encryption;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform;

/// <summary>
/// Gives an operator a new authenticator secret: <c>dotnet run -- reset-platform-user-mfa &lt;email&gt;</c>.
///
/// The recovery path for a lost or replaced phone, and deliberately the only one: there is no
/// self-service reset, because an operator reaches every tenant and "I lost my phone" is exactly
/// what an attacker holding only the password would say. Being on the machine is the proof.
///
/// Every session the operator holds is revoked in the same save. A reset may mean the old device
/// is in someone else's hands, and a session signed in with it must not outlive the secret.
/// </summary>
public static class ResetPlatformUserMfa
{
    public static async Task<int> RunAsync(string connectionString, KeyEncryptionKey kek, string[] args)
    {
        var email = args.SkipWhile(a => a != "reset-platform-user-mfa").Skip(1).FirstOrDefault();

        await using var db = PlatformDbContext.ForConnection(connectionString);
        var keyRing = await KeyRing.LoadOrCreateAsync(db, kek, TimeProvider.System);

        var (exitCode, message) = await ResetAsync(db, keyRing, email, TimeProvider.System);

        (exitCode == 0 ? Console.Out : Console.Error).WriteLine(message);
        return exitCode;
    }

    internal static async Task<(int ExitCode, string Message)> ResetAsync(
        PlatformDbContext db, KeyRing keyRing, string? emailArgument, TimeProvider clock)
    {
        var email = emailArgument?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email))
            return (1, "Usage: dotnet run -- reset-platform-user-mfa <email>");

        // The operator's row is LOCKED before anything else is read, and held until the commit below.
        // A sign-in in flight takes the same lock to use its code and create its session, so it
        // either finishes first (and its session is among those revoked here) or waits and then
        // finds the secret changed (and gets no session). See CreateSessionWithCodeAsync. Raw SQL
        // because EF has no FOR UPDATE; platform tables carry no tenant filter to bypass.
        await using var transaction = await db.Database.BeginTransactionAsync();

        var user = await db.PlatformUsers
            .FromSql($"SELECT * FROM platform.platform_user WHERE email = {email} FOR UPDATE")
            .SingleOrDefaultAsync();
        if (user is null)
            return (1, $"reset-platform-user-mfa: no operator {email}");

        var now = clock.GetUtcNow();
        var secret = OperatorMfa.Enroll(user, keyRing);

        var liveSessions = await db.PlatformSessions
            .Where(s => s.PlatformUserId == user.Id && s.RevokedAt == null)
            .ToListAsync();
        foreach (var session in liveSessions) session.RevokedAt = now;

        db.AuditLogs.Add(new PlatformAuditLog
        {
            // No acting operator: this runs on the machine, outside any operator session.
            Action = "operator.mfa_reset",
            Detail = $"{email}: {liveSessions.Count} session(s) revoked",
            CreatedAt = now,
        });

        // One transaction: the new secret, the revocations and the audit row commit together or not at all.
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return (0,
            $"reset-platform-user-mfa: new authenticator for {user.PublicId} ({email}); " +
            $"{liveSessions.Count} session(s) revoked\n" + CreatePlatformUser.SecretNotice(email, secret));
    }
}
