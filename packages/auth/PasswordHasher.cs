using System.Security.Cryptography;
using System.Text;

namespace AppPlatform.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256, with the cost and salt stored alongside the hash.
///
/// The format is VERSIONED — <c>v1.iterations.salt.hash</c> — so the work factor can be raised
/// later without invalidating every existing password. A bare hash with the cost baked into
/// code is the version of this that cannot be upgraded: raising the cost makes every stored
/// hash unverifiable, so in practice nobody raises it.
/// </summary>
public static class PasswordHasher
{
    /// <summary>OWASP's floor for PBKDF2-HMAC-SHA256. Raise it, never lower it.</summary>
    public const int DefaultIterations = 600_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Version = "v1";

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);

        return $"{Version}.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Returns false for a malformed or unknown-version hash rather than throwing. A corrupt
    /// row must fail one sign-in, not take the endpoint down with a 500 that tells an attacker
    /// which accounts have bad data.
    /// </summary>
    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;

        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != Version) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 1) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length != HashBytes) return false;

        // Fixed-time: an ordinary comparison returns as soon as two bytes differ, which leaks
        // how much of a guess was right.
        return CryptographicOperations.FixedTimeEquals(
            Derive(password, salt, iterations), expected);
    }

    /// <summary>
    /// True when a stored hash was produced with a lower cost than we now require, so the
    /// caller can transparently re-hash on a successful sign-in — the only moment the plaintext
    /// is available to do it.
    /// </summary>
    public static bool NeedsRehash(string? stored, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(stored)) return true;

        var parts = stored.Split('.');
        return parts.Length != 4
            || parts[0] != Version
            || !int.TryParse(parts[1], out var stored_iterations)
            || stored_iterations < iterations;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
