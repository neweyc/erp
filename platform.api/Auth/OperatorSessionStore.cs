using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform.Auth;

public interface IOperatorSessionStore
{
    Task<OperatorSessionContext?> FindAsync(Guid sessionId, CancellationToken ct = default);
    Task TouchAsync(Guid sessionId, CancellationToken ct = default);
    Task<PlatformUser?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task RevokeAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Upgrades a hash stored at an older cost, on successful sign-in.</summary>
    Task UpdatePasswordHashAsync(Guid platformUserId, string hash, CancellationToken ct = default);

    /// <summary>
    /// Uses an authenticator code and creates the session it earned, in ONE transaction, or does
    /// neither and returns null. Null means the code was already used (this step or a later one), or
    /// the operator's secret changed after the code was checked against it — a reset in between.
    /// </summary>
    Task<Guid?> CreateSessionWithCodeAsync(
        Guid platformUserId, int checkedSecretVersion, long step, DateTimeOffset now, CancellationToken ct = default);
}

public sealed class EFOperatorSessionStore(PlatformDbContext db) : IOperatorSessionStore
{
    public Task<OperatorSessionContext?> FindAsync(Guid sessionId, CancellationToken ct = default)
        => db.PlatformSessions
            .Where(s => s.Id == sessionId)
            .Join(db.PlatformUsers, s => s.PlatformUserId, u => u.Id, (s, u) => new OperatorSessionContext
            {
                SessionId = s.Id,
                PlatformUserId = u.Id,
                Email = u.Email,
                UserActive = u.Active,
                MfaSatisfied = s.MfaSatisfied,
                LastSeenAt = s.LastSeenAt,
                AbsoluteExpiry = s.AbsoluteExpiry,
                RevokedAt = s.RevokedAt,
            })
            .FirstOrDefaultAsync(ct);

    public Task TouchAsync(Guid sessionId, CancellationToken ct = default)
        => db.PlatformSessions
            .Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, DateTimeOffset.UtcNow), ct);

    public Task<PlatformUser?> FindByEmailAsync(string email, CancellationToken ct = default)
        => db.PlatformUsers.FirstOrDefaultAsync(u => u.Email == email, ct);

    public Task RevokeAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default)
        => db.PlatformSessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);

    /// <summary>
    /// The conditional UPDATE is the check: this step must be later than the last one used, and the
    /// secret must still be the one the code was verified against. Check and write in one statement,
    /// so two sign-ins racing with one code cannot both pass.
    ///
    /// The UPDATE also locks the operator's row until the transaction ends, and the session is
    /// inserted before it does. reset-platform-user-mfa takes the same lock before reading sessions,
    /// so the two cannot interleave: if the reset locks first, this UPDATE waits, then sees the new
    /// secret version and matches nothing; if this locks first, the reset waits and then finds, and
    /// revokes, the session made here.
    ///
    /// ExecuteUpdate skips SaveChanges, which is safe here: platform tables have no tenant filter and
    /// no audit interceptor, and the sign-in writes its own operator audit row.
    /// </summary>
    public async Task<Guid?> CreateSessionWithCodeAsync(
        Guid platformUserId, int checkedSecretVersion, long step, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var used = await db.PlatformUsers
            .Where(u => u.Id == platformUserId
                        && u.TotpSecretVersion == checkedSecretVersion
                        && (u.TotpLastUsedStep == null || u.TotpLastUsedStep < step))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.TotpLastUsedStep, step), ct);

        // Nothing matched: disposing the transaction rolls it back, and no session exists.
        if (used != 1) return null;

        var session = new PlatformSession
        {
            PlatformUserId = platformUserId,
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiry = now + OperatorSessionEvaluator.AbsoluteLifetime,
        };
        db.PlatformSessions.Add(session);
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return session.Id;
    }

    public Task UpdatePasswordHashAsync(Guid platformUserId, string hash, CancellationToken ct = default)
        => db.PlatformUsers
            .Where(u => u.Id == platformUserId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, hash), ct);
}
