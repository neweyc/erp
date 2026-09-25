using System.Security.Claims;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Auth;

public static class SignInFeature
{
    public record SignInCommand(string? Email, string? Password);

    public record SignInOutcome(Guid? SessionId, string? ProblemCode);

    public class SignInCommandHandler(
        IOperatorSessionStore sessions,
        ITenantAuditWriter audit,
        TimeProvider clock)
    {
        public async Task<SignInOutcome> Handle(SignInCommand cmd, CancellationToken ct = default)
        {
            var email = cmd.Email?.Trim().ToLowerInvariant();
            var password = cmd.Password;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return Failure(email, "missing credentials");

            var user = await sessions.FindByEmailAsync(email, ct);

            // Verified even when no such account exists, against a throwaway hash. Returning
            // early on an unknown address makes sign-in measurably faster for addresses that do
            // not exist, which enumerates the operator accounts.
            //
            // The dummy is hashed at DefaultIterations, so this only holds while stored hashes
            // are ALSO at DefaultIterations — otherwise the defence inverts and an unknown
            // address becomes measurably SLOWER, which is just as enumerable. The rehash below
            // is what keeps that true.
            var hash = user?.PasswordHash ?? DummyHash.Value;
            var valid = PasswordHasher.Verify(password, hash);

            if (user is null || !valid || !user.Active)
            {
                // One problem code for all three. Distinguishing "no such operator" from "wrong
                // password" from "deactivated" tells an attacker which half of the guess was
                // right.
                return Failure(email, user is null ? "unknown operator" : !valid ? "bad password" : "deactivated");
            }

            // A successful sign-in is the only moment the plaintext is available, so it is the
            // only moment a hash stored at an older cost can be upgraded. Skipping this leaves
            // old hashes at their original cost forever — weaker than intended, and enough to
            // break the timing defence above.
            if (PasswordHasher.NeedsRehash(user.PasswordHash))
            {
                user.PasswordHash = PasswordHasher.Hash(password);
                await sessions.UpdatePasswordHashAsync(user.Id, user.PasswordHash, ct);
            }

            var sessionId = await sessions.CreateSessionAsync(user.Id, clock.GetUtcNow(), ct);

            audit.Write(new PlatformAuditLog
            {
                PlatformUserId = user.Id,
                Action = "operator.signed_in",
                Detail = email,
                CreatedAt = clock.GetUtcNow(),
            });

            return new SignInOutcome(sessionId, null);
        }

        private SignInOutcome Failure(string? email, string reason)
        {
            // Audited even on failure: repeated failures against an operator account are the
            // signal that matters most on this surface, and they are invisible if only
            // successes are recorded.
            audit.Write(new PlatformAuditLog
            {
                Action = "operator.sign_in_failed",
                Detail = $"{email}: {reason}",
                CreatedAt = clock.GetUtcNow(),
            });

            return new SignInOutcome(null, OperatorProblems.InvalidCredentials);
        }
    }

    /// <summary>A real hash of a random secret, computed once, purely to burn equivalent time.</summary>
    private static class DummyHash
    {
        public static readonly string Value = PasswordHasher.Hash(Guid.NewGuid().ToString());
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/api/platform/v1/auth/sign-in",
            async (
                SignInCommand cmd,
                HttpContext http,
                [FromServices] IOperatorSessionStore sessions,
                [FromServices] ITenantAuditWriter audit,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new SignInCommandHandler(sessions, audit, clock);
                var outcome = await handler.Handle(cmd, ct);

                await audit.FlushAsync(ct);

                if (outcome.SessionId is not { } sessionId)
                    return CommandResult.Forbidden(outcome.ProblemCode!, "Sign-in failed.").CreateIResult();

                var identity = new ClaimsIdentity(
                    [new Claim(SessionCookie.SessionIdClaim, sessionId.ToString())],
                    SessionCookie.Operator.SchemeName);

                await http.SignInAsync(
                    SessionCookie.Operator.SchemeName, new ClaimsPrincipal(identity));

                // Issued WITH the session, not later. A browser holding a session and no token
                // has every mutation refused as csrf_failed, which looks like a broken
                // deployment rather than a missing cookie.
                CsrfToken.Issue(http);

                return Results.Ok(new { signedIn = true });
            })
            .AllowAnonymous();
    }
}
