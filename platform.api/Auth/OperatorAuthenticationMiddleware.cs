using System.Security.Claims;
using AppPlatform.Auth;
using Microsoft.AspNetCore.Http;

namespace AppPlatform.Platform.Auth;

/// <summary>
/// Resolves the operator cookie into a principal on every request.
///
/// A separate middleware from the tenant one, using a separate cookie name, session table and
/// signing key. The isolation is the point: a console session must never authenticate a tenant
/// API, or the reverse.
/// </summary>
public sealed class OperatorAuthenticationMiddleware(
    RequestDelegate next,
    TimeProvider clock)
{
    public async Task InvokeAsync(
        HttpContext context, IOperatorSessionStore sessions, OperatorContext operatorContext)
    {
        var raw = context.User.FindFirstValue(SessionCookie.SessionIdClaim);

        if (!Guid.TryParse(raw, out var sessionId))
        {
            // Anonymous is not a failure: sign-in itself has no session yet.
            await next(context);
            return;
        }

        var stored = await sessions.FindAsync(sessionId, context.RequestAborted);
        var (principal, problem) = OperatorSessionEvaluator.Evaluate(stored, clock.GetUtcNow());

        if (principal is null)
        {
            context.Response.Cookies.Delete(SessionCookie.Operator.Name);
            await AuthProblemResponse.WriteAsync(context, problem!);
            return;
        }

        operatorContext.Set(principal);

        // Touched on every request rather than throttled: operator traffic is a rounding error
        // next to tenant traffic, and a 30-minute idle window with a stale timestamp fires
        // EARLY — signing an operator out mid-incident.
        try
        {
            await sessions.TouchAsync(sessionId, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Best effort. A missed touch costs an early timeout; a failed request costs work.
        }

        await next(context);
    }
}
