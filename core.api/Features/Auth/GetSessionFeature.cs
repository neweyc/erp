using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Services;
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

    public class GetSessionQueryHandler(IAuthService auth)
    {
        public async Task<CommandResult> Handle(Caller caller, CancellationToken ct = default)
        {
            // The account can disappear between the session being validated and this read —
            // deactivation and deletion both revoke sessions, but not atomically with an
            // in-flight request. Never answer with an empty successful session.
            var user = await auth.FindUserAsync(caller.RequireUserId(), ct);
            if (user is null)
                return CommandResult.Forbidden(AuthProblems.NotSignedIn, "Not signed in.");

            return CommandResult.Ok(new SessionModel(
                user.PublicId, user.Email, caller.Role,
                // The CALLER's tenant, passed explicitly. This read once had no id and relied on a
                // filter the tenant table does not have, so every tenant was shown the name of
                // whichever tenant was provisioned first. Found by the second-tenant e2e (A2).
                await auth.TenantNameAsync(caller.TenantId, ct),
                // Straight from the session contract, which reads the platform's entitlement
                // rows. The shell gates nav on this; the API gates access independently.
                caller.LicensedApps,
                caller.TenantSuspended));
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/auth/session",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] IAuthService auth,
                CancellationToken ct) =>
            {
                var handler = new GetSessionQueryHandler(auth);
                // Require: the endpoint is authorized, so reaching it without a caller is a
                // wiring fault rather than a 401 to be rendered.
                return (await handler.Handle(callerContext.Require(), ct)).CreateIResult();
            })
            .RequireAuthorization();
    }
}
