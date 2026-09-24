using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace AppPlatform.Auth;

/// <summary>
/// Turns the cookie's session id into a <see cref="Caller"/>, on EVERY request.
///
/// Revalidating per request is what makes revocation, deactivation, role change,
/// suspension, and entitlement change take effect immediately rather than at cookie
/// expiry. The cost is one indexed function call per request; the alternative is a
/// window during which a sacked administrator still has an administrator's cookie.
/// </summary>
public sealed class SessionAuthenticationMiddleware(
    RequestDelegate next,
    ISessionStore sessions,
    TimeProvider clock,
    SessionCookie cookie)
{
    public async Task InvokeAsync(HttpContext context, CallerContext callerContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callerContext);

        var sessionId = ReadSessionId(context.User);

        // Anonymous is not a failure here. Sign-in, invite acceptance, and password reset
        // have no session yet; endpoints that need one say so through authorization.
        if (sessionId is not { } id)
        {
            await next(context);
            return;
        }

        var stored = await sessions.FindAsync(id, context.RequestAborted);
        var result = SessionEvaluator.Evaluate(
            stored,
            context.User.FindFirstValue(SessionCookie.RoleClaim),
            clock.GetUtcNow());

        if (!result.Succeeded)
        {
            // Delete the cookie on an authentication failure so the browser stops
            // presenting a credential that will never work again. Suspension never lands
            // here — it authenticates successfully — so a suspended tenant keeps its
            // cookie and finds its users still signed in when resumed.
            context.Response.Cookies.Delete(cookie.Name);
            await AuthProblemResponse.WriteAsync(context, result.ProblemCode!);
            return;
        }

        callerContext.SetCaller(result.Caller!);

        await TouchAsync(context, stored!, id);
        await next(context);
    }

    private static Guid? ReadSessionId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(SessionCookie.SessionIdClaim), out var id) ? id : null;

    private async Task TouchAsync(HttpContext context, SessionContext stored, Guid sessionId)
    {
        var path = context.Request.Path.Value ?? "/";

        var shouldTouch = path.Equals(ActivityPolicy.ActivityPingPath, StringComparison.OrdinalIgnoreCase)
            // The explicit ping bypasses the throttle. The browser watches keystrokes and
            // the server watches requests; a long form produces plenty of the first and
            // none of the second, and the throttle is exactly what lets the two clocks
            // drift until a user's save is rejected.
            || ActivityPolicy.ShouldTouch(path, stored.LastSeenAt, clock.GetUtcNow(), stored.IdleTimeoutMinutes);

        if (!shouldTouch) return;

        try
        {
            await sessions.TouchAsync(sessionId, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Best effort by design. The cost of a missed touch is a session that times
            // out slightly early; the cost of failing the request is losing the user's
            // work because bookkeeping did not commit.
        }
    }
}
