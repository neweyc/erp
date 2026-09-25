using AppPlatform.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AppPlatform.Core.Services;

public class EFAuthService(CoreDbContext db) : IAuthService
{
    public Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

    public Task<UserToken?> FindTokenAsync(
        string tokenHash, TokenPurpose purpose, CancellationToken ct = default)
        // IgnoreQueryFilters: accepting an invitation happens BEFORE there is a session, so
        // there is no ambient tenant to filter by. The token hash is 256 bits of randomness and
        // is itself the authorization — which is why this path is reviewed as carefully as any
        // anonymous endpoint, and why the tenant is taken FROM the token rather than the caller.
        => db.UserTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.Purpose == purpose, ct);

    public Task<User?> FindUserAsync(Guid userId, CancellationToken ct = default)
        => db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task<Session?> FindSessionAsync(Guid sessionId, CancellationToken ct = default)
        => db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public Task<string> TenantNameAsync(int tenantId, CancellationToken ct = default)
        => db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(ct);

    public Task RevokeSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default)
        => db.Sessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);

    public void AddSession(Session session) => db.Sessions.Add(session);

    public void AddToken(UserToken token) => db.UserTokens.Add(token);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
