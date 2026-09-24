using Microsoft.AspNetCore.Http;

namespace AppPlatform.Auth;

/// <summary>
/// Narrows what a suspended tenant may reach. This runs AFTER authentication and is
/// authorization, not authentication — the distinction is load-bearing.
///
/// Rejecting suspension during authentication would discard the principal, and the data
/// export a suspended customer is explicitly still promised would then have no
/// authenticated caller left to authorize. So the principal survives, the cookie survives,
/// and only the route is refused.
/// </summary>
public sealed class SuspensionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICallerContext callerContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callerContext);

        if (callerContext.Caller is { } caller)
        {
            var problem = SuspensionPolicy.Evaluate(caller, context.Request.Path.Value ?? "/");

            if (problem is not null)
            {
                await AuthProblemResponse.WriteAsync(context, problem);
                return;
            }
        }

        await next(context);
    }
}
