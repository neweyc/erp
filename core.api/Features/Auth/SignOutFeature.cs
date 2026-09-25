using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Auth;

public static class SignOutFeature
{
    public class SignOutCommandHandler(IAuthService auth, TimeProvider clock)
    {
        /// <returns>True when a server-side session was revoked.</returns>
        public async Task<bool> Handle(Caller? caller, CancellationToken ct = default)
        {
            // Only a cookie session has a row to revoke. An API key has no SessionId, and
            // `?? Guid.Empty` would revoke nothing while the endpoint still answered 204 —
            // reporting success for work that did not happen.
            if (caller?.SessionId is not { } sessionId) return false;

            // Revoked server-side as well as cleared in the browser: a copied cookie must stop
            // validating after sign-out.
            await auth.RevokeSessionAsync(sessionId, clock.GetUtcNow(), ct);
            return true;
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/core/v1/auth/sign-out",
            async (
                HttpContext http,
                [FromServices] ICallerContext callerContext,
                [FromServices] IAuthService auth,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new SignOutCommandHandler(auth, clock);
                var revoked = await handler.Handle(callerContext.Caller, ct);

                // The cookie is cleared either way: a caller asking to sign out should end up
                // signed out even if there was no session row to revoke.
                await http.SignOutAsync(SessionCookie.Tenant.SchemeName);
                CsrfToken.Clear(http);

                return Results.Ok(new { revoked });
            })
            .RequireAuthorization();
    }
}
