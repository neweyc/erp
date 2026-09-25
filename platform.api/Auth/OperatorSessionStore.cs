using AppPlatform.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Platform.Auth;

public interface IOperatorSessionStore
{
    Task<OperatorSessionContext?> FindAsync(Guid sessionId, CancellationToken ct = default);
    Task TouchAsync(Guid sessionId, CancellationToken ct = default);
    Task<PlatformUser?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<Guid> CreateSessionAsync(Guid platformUserId, DateTimeOffset now, CancellationToken ct = default);
    Task RevokeAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Upgrades a hash stored at an older cost, on successful sign-in.</summary>
    Task UpdatePasswordHashAsync(Guid platformUserId, string hash, CancellationToken ct = default);
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

    public async Task<Guid> CreateSessionAsync(
        Guid platformUserId, DateTimeOffset now, CancellationToken ct = default)
    {
        var session = new PlatformSession
        {
            PlatformUserId = platformUserId,
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiry = now + OperatorSessionEvaluator.AbsoluteLifetime,
        };

        db.PlatformSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    public Task RevokeAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default)
        => db.PlatformSessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);

    public Task UpdatePasswordHashAsync(Guid platformUserId, string hash, CancellationToken ct = default)
        => db.PlatformUsers
            .Where(u => u.Id == platformUserId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.PasswordHash, hash), ct);
}
