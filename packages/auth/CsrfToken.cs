using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace AppPlatform.Auth;

/// <summary>
/// Issues the double-submit token.
///
/// Issued at SIGN-IN, alongside the session cookie. Without this a fresh browser holds a valid
/// session and no token, so every mutation is refused with csrf_failed — an application that
/// signs you in and then rejects everything you do, which reads as a broken deployment rather
/// than a missing cookie.
/// </summary>
public static class CsrfToken
{
    public static string Issue(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // URL-SAFE, not plain base64. A cookie value containing '+' or '/' is percent-encoded
        // on the way out, so the page reading document.cookie sees the encoded form while the
        // server decodes what comes back — and the two never match. It would have failed only
        // when the random bytes happened to produce one of those characters, which is the worst
        // kind of intermittent.
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

        context.Response.Cookies.Append(CsrfPolicy.CookieName, token, new CookieOptions
        {
            // Readable by script, unlike the session cookie: the page has to copy it into the
            // request header, which is the entire mechanism of double-submit. Its secrecy from
            // JavaScript is not what protects it — an attacker's site cannot read it because it
            // belongs to another origin.
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            // Session-scoped, so it expires with the browser session rather than outliving the
            // authentication cookie it is paired with.
            IsEssential = true,
        });

        return token;
    }

    /// <summary>Cleared on sign-out, so a stale token cannot be paired with a new session.</summary>
    public static void Clear(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Delete(CsrfPolicy.CookieName);
    }
}
