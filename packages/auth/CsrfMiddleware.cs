using Microsoft.AspNetCore.Http;

namespace AppPlatform.Auth;

/// <summary>
/// Whether the current request was authenticated by a COOKIE, which is the only case CSRF
/// applies to.
///
/// An interface because the two surfaces carry different identities: tenant requests populate
/// a <see cref="CallerContext"/>, operator requests populate their own. Wiring the middleware
/// directly to the tenant context made it inert on the operator console — it saw no caller,
/// concluded the request was not cookie-authenticated, and waved every mutation through.
/// </summary>
public interface ICookieAuthenticationState
{
    bool IsCookieAuthenticated { get; }
}

/// <summary>
/// Double-submit CSRF, enforced centrally rather than per endpoint — a new endpoint must
/// not be able to forget it, and an opt-in check is one forgotten attribute away from a
/// hole nobody can see in review.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICookieAuthenticationState state)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);

        var problem = CsrfPolicy.Evaluate(
            context.Request.Method,
            isCookieAuthenticated: state.IsCookieAuthenticated,
            headerToken: context.Request.Headers[CsrfPolicy.HeaderName].FirstOrDefault(),
            cookieToken: context.Request.Cookies[CsrfPolicy.CookieName]);

        if (problem is not null)
        {
            await AuthProblemResponse.WriteAsync(context, problem);
            return;
        }

        await next(context);
    }
}
