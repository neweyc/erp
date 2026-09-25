using System.Security.Cryptography;
using System.Text;
using AppPlatform.Core.Data;

namespace AppPlatform.Core.Services;

public interface IAuthService
{
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> FindUserAsync(Guid userId, CancellationToken ct = default);
    Task<UserToken?> FindTokenAsync(string tokenHash, TokenPurpose purpose, CancellationToken ct = default);
    Task<Session?> FindSessionAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>
    /// The name of the given tenant. The id is REQUIRED because the tenant table is not
    /// tenant-scoped — it is what tenants are scoped by — so no query filter narrows it, and a
    /// read without an explicit id returns whichever tenant the database yields first.
    /// </summary>
    Task<string> TenantNameAsync(int tenantId, CancellationToken ct = default);
    Task RevokeSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default);
    void AddSession(Session session);
    void AddToken(UserToken token);
    Task SaveAsync(CancellationToken ct = default);
}

/// <summary>
/// Tokens are generated here and stored only as a hash.
///
/// SHA-256 rather than a password hash: a token is 256 bits of cryptographic randomness with a
/// short life, so there is nothing to brute-force and no need for a work factor. Passwords need
/// one because people choose them.
/// </summary>
public static class TokenGenerator
{
    public static (string Plaintext, string Hash) Create()
    {
        var plaintext = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (plaintext, HashOf(plaintext));
    }

    public static string HashOf(string plaintext)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
}
