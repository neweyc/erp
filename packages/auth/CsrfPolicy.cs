using System.Security.Cryptography;
using System.Text;

namespace AppPlatform.Auth;

/// <summary>
/// Double-submit CSRF, enforced centrally so a new endpoint cannot forget it.
/// </summary>
public static class CsrfPolicy
{
    public const string HeaderName = "X-CSRF-Token";
    public const string CookieName = "ap_csrf";

    /// <summary>
    /// Methods that need no token. Everything else does.
    ///
    /// The test is SAFE versus UNSAFE, never idempotent versus not. PUT and DELETE are
    /// idempotent and change state, so a rule written around idempotency leaves most of a
    /// REST API's write surface unprotected — which is the wording this replaced.
    /// </summary>
    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS", "TRACE"];

    public static bool IsSafe(string method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return SafeMethods.Contains(method, StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="isCookieAuthenticated">
    /// False for an API key. A browser does not attach a bearer token of its own accord,
    /// so a key-authenticated call carries no cross-site exposure and requiring a token
    /// there would only break integrations.
    /// </param>
    /// <returns>Null when the request may proceed; otherwise the problem code for a 403.</returns>
    public static string? Evaluate(
        string method, bool isCookieAuthenticated, string? headerToken, string? cookieToken)
    {
        if (IsSafe(method)) return null;
        if (!isCookieAuthenticated) return null;

        if (string.IsNullOrEmpty(headerToken) || string.IsNullOrEmpty(cookieToken))
            return AuthProblem.CsrfFailed;

        return TokensMatch(headerToken, cookieToken) ? null : AuthProblem.CsrfFailed;
    }

    /// <summary>
    /// Fixed-time comparison. An ordinary string compare returns as soon as two bytes
    /// differ, which leaks how much of a guessed token was correct.
    /// </summary>
    private static bool TokensMatch(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
