using Microsoft.AspNetCore.Http;

namespace AppPlatform.Auth;

/// <summary>
/// Double-submit CSRF, enforced centrally rather than per endpoint — a new endpoint must
/// not be able to forget it, and an opt-in check is one forgotten attribute away from a
/// hole nobody can see in review.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICallerContext callerContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callerContext);

        var problem = CsrfPolicy.Evaluate(
            context.Request.Method,
            isCookieAuthenticated: callerContext.Caller?.Kind is PrincipalKind.User,
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
