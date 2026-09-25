using AppPlatform.Audit;
using System.Security.Claims;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Auth;

public static class SignInFeature
{
    public record SignInCommand(string? Email, string? Password);
    public record SignInOutcome(Guid? SessionId, string? Role, string? ProblemCode);

    public class SignInCommandHandler(IAuthService auth, IAuditActorScope auditActor, TimeProvider clock)
    {
        /// <summary>Matches the tenant-user lifetime in docs/auth-and-access.md.</summary>
        public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

        public async Task<SignInOutcome> Handle(
            int tenantId, SignInCommand cmd, CancellationToken ct = default)
        {
            var email = cmd.Email?.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(cmd.Password))
                return Failed();

            var user = await auth.FindByEmailAsync(email, ct);

            // Verified even when no account exists, against a hash of the same cost. Returning
            // early makes sign-in measurably faster for addresses that do not exist, which
            // enumerates the tenant's users.
            var hash = user?.PasswordHash ?? DummyHash.Value;
            var valid = PasswordHasher.Verify(cmd.Password, hash);

            // One outcome for unknown, wrong, invited and deactivated. Telling them apart says
            // which half of a guess was right — and "that address exists but is not yet active"
            // is itself worth knowing to an attacker.
            if (user is null || !valid || user.Status != UserStatus.Active) return Failed();

            // The user is the actor for anything this sign-in changes — today, only a rehash below.
            // Declared here because the session that would normally say so does not exist yet.
            auditActor.UseActor(AuditActor.User(user.Id));

            if (PasswordHasher.NeedsRehash(user.PasswordHash))
            {
                // The only moment the plaintext is available, so the only moment an older cost
                // can be upgraded. It is also what keeps the timing defence above honest.
                user.PasswordHash = PasswordHasher.Hash(cmd.Password);
            }

            var session = new Session
            {
                UserId = user.Id,
                CreatedAt = clock.GetUtcNow(),
                LastSeenAt = clock.GetUtcNow(),
                AbsoluteExpiry = clock.GetUtcNow() + SessionLifetime,
            };

            auth.AddSession(session);
            await auth.SaveAsync(ct);

            return new SignInOutcome(session.Id, user.Role, null);
        }

        private static SignInOutcome Failed()
            => new(null, null, AuthProblems.InvalidCredentials);
    }

    private static class DummyHash
    {
        public static readonly string Value = PasswordHasher.Hash(Guid.NewGuid().ToString());
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/core/v1/auth/sign-in",
            async (
                SignInCommand cmd,
                HttpContext http,
                [FromServices] IAuthService auth,
                [FromServices] IBackgroundTenantScope tenantScope,
                [FromServices] ITenantResolver tenants,
                [FromServices] IAuditActorScope auditActor,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                // Sign-in is anonymous, so there is no session to take a tenant from. It is
                // resolved from the REQUEST HOST, never from the body: a client-supplied tenant
                // id would let anyone choose whose users they are attacking.
                var tenantId = await tenants.ResolveAsync(http.Request.Host.Host, ct);
                if (tenantId is not { } id)
                    return CommandResult.Forbidden(AuthProblems.InvalidCredentials, "Sign-in failed.").CreateIResult();

                tenantScope.UseTenant(id);

                var handler = new SignInCommandHandler(auth, auditActor, clock);
                var outcome = await handler.Handle(id, cmd, ct);

                if (outcome.SessionId is not { } sessionId)
                    return CommandResult.Forbidden(outcome.ProblemCode!, "Sign-in failed.").CreateIResult();

                var identity = new ClaimsIdentity(
                    [
                        new Claim(SessionCookie.SessionIdClaim, sessionId.ToString()),
                        // Compared against the live row on every request, so a demotion takes
                        // effect immediately rather than at cookie expiry.
                        new Claim(SessionCookie.RoleClaim, outcome.Role!),
                    ],
                    SessionCookie.Tenant.SchemeName);

                await http.SignInAsync(SessionCookie.Tenant.SchemeName, new ClaimsPrincipal(identity));

                // Issued WITH the session. A browser holding a session and no token has every
                // mutation refused as csrf_failed, which reads as a broken deployment.
                CsrfToken.Issue(http);

                return Results.Ok(new { signedIn = true });
            })
            .AllowAnonymous();
    }
}
