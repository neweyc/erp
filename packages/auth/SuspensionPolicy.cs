namespace AppPlatform.Auth;

/// <summary>
/// What a suspended tenant may still reach. This is AUTHORIZATION, running after
/// authentication has already established the principal.
///
/// The distinction is not pedantry: rejecting suspension at authentication discards the
/// identity, and the export a suspended customer is promised then has no authenticated
/// caller to authorize. Suspension is a 403 with an allowlist, and the cookie is left
/// intact so that resuming a tenant finds its users still signed in.
/// </summary>
public static class SuspensionPolicy
{
    /// <summary>
    /// Prefixes reachable while suspended: authentication (so they can sign in and see
    /// why) and data export (so they can leave with what is theirs). Matched
    /// case-insensitively on segment boundaries, so `/api/core/v1/authorised-signers`
    /// cannot slip through on a prefix of `/api/core/v1/auth`.
    /// </summary>
    private static readonly string[] AllowedPrefixes =
    [
        "/api/core/v1/auth",
        "/api/core/v1/tenant/export",
    ];

    public static bool IsAllowedWhileSuspended(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.TrimEnd('/');

        return AllowedPrefixes.Any(prefix =>
            normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <returns>Null when the request may proceed; otherwise the problem code for a 403.</returns>
    public static string? Evaluate(Caller caller, string path)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.TenantSuspended) return null;

        return IsAllowedWhileSuspended(path) ? null : AuthProblem.TenantSuspended;
    }
}
