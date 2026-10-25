using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Auth;

/// <summary>
/// Ends the operator's session. Revoked on the server as well as cleared in the browser, so a
/// copied cookie stops working too; an operator session reaches every tenant, and closing the
/// browser tab is not the same as signing out.
/// </summary>
public static class SignOutFeature
{
    public class SignOutCommandHandler(IOperatorSessionStore sessions, ITenantAuditWriter audit, TimeProvider clock)
    {
        public async Task Handle(OperatorPrincipal principal, CancellationToken ct = default)
        {
            await sessions.RevokeAsync(principal.SessionId, clock.GetUtcNow(), ct);

            audit.Write(new PlatformAuditLog
            {
                PlatformUserId = principal.PlatformUserId,
                Action = "operator.signed_out",
                CreatedAt = clock.GetUtcNow(),
            });
            await audit.FlushAsync(ct);
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/platform/v1/auth/sign-out",
            async (
                HttpContext http,
                [FromServices] IOperatorContext operatorContext,
                [FromServices] IOperatorSessionStore sessions,
                [FromServices] ITenantAuditWriter audit,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                await new SignOutCommandHandler(sessions, audit, clock).Handle(operatorContext.Require(), ct);

                await http.SignOutAsync(SessionCookie.Operator.SchemeName);
                CsrfToken.Clear(http);

                return Results.Ok(new { signedOut = true });
            })
            .RequireAuthorization();
    }
}
