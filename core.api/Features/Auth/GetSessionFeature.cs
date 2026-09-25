using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Auth;

/// <summary>
/// What the shell asks for on load: who is signed in, and what the tenant has licensed.
/// </summary>
public static class GetSessionFeature
{
    public record SessionModel(
        string UserId, string Email, string Role, string TenantName,
        IReadOnlyList<string> LicensedApps, bool TenantSuspended);

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/auth/session",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] IAuthService auth,
                CancellationToken ct) =>
            {
                var caller = callerContext.Caller;

                // 401, not an empty 200. A shell that cannot tell "signed out" from "signed in
                // with nothing" renders an empty app to someone who simply needs to sign in.
                if (caller is null)
                {
                    return CommandResult.Forbidden(AuthProblems.NotSignedIn, "Not signed in.")
                        .CreateIResult();
                }

                var user = await auth.FindUserAsync(caller.RequireUserId(), ct);
                if (user is null)
                    return CommandResult.Forbidden(AuthProblems.NotSignedIn, "Not signed in.").CreateIResult();

                return Results.Ok(new SessionModel(
                    user.PublicId, user.Email, caller.Role,
                    await auth.TenantNameAsync(ct),
                    // Straight from the session contract, which reads the platform's entitlement
                    // rows. The shell gates nav on this; the API gates access independently.
                    caller.LicensedApps,
                    caller.TenantSuspended));
            })
            .RequireAuthorization();
    }
}

public static class SignOutFeature
{
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
                if (callerContext.Caller is { } caller)
                {
                    // Revoked server-side, not merely un-cookied. Deleting the cookie alone
                    // leaves a session row that still validates for anyone who kept a copy.
                    await auth.RevokeSessionAsync(caller.SessionId ?? Guid.Empty, clock.GetUtcNow(), ct);
                }

                await http.SignOutAsync(SessionCookie.Tenant.SchemeName);
                CsrfToken.Clear(http);

                return Results.NoContent();
            })
            .RequireAuthorization();
    }
}
