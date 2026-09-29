using System.Security.Claims;
using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Encryption;
using AppPlatform.Platform.Auth;
using AppPlatform.Platform.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Platform.Features.Auth;

public static class SignInFeature
{
    /// <summary>
    /// Password and authenticator code together, in one request. No session exists until both
    /// pass, so there is never a half-signed-in operator. A client that omits the code learns only
    /// AFTER the password is verified that one is needed (mfa_required), and asks for it.
    /// </summary>
    public record SignInCommand(string? Email, string? Password, string? Code = null);

    public record SignInOutcome(Guid? SessionId, string? ProblemCode);

    public class SignInCommandHandler(
        IOperatorSessionStore sessions,
        ITenantAuditWriter audit,
        KeyRing keyRing,
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

            // The second factor. Checked only after the password, so every answer before this point
            // is the same invalid_credentials, and nothing here tells a guesser whether an account
            // exists. Past this point the caller has proved the password, so saying what is missing
            // tells them nothing they do not already know.
            if (!user.MfaEnabled)
                return Failure(email, "no authenticator enrolled", OperatorProblems.MfaNotEnrolled);

            if (string.IsNullOrWhiteSpace(cmd.Code))
                return Failure(email, "code required", OperatorProblems.MfaRequired);

            var step = Totp.Verify(OperatorMfa.ReadSecret(user, keyRing), cmd.Code, clock.GetUtcNow(), user.TotpLastUsedStep);
            if (step is null)
                return Failure(email, "wrong or reused code", OperatorProblems.MfaCodeInvalid);

            // The code is used and the session created together, and only while the secret is still
            // the one the code was checked against. Of two sign-ins racing with one code, exactly one
            // gets a session; a reset in between leaves neither with one.
            if (await sessions.CreateSessionWithCodeAsync(user.Id, user.TotpSecretVersion, step.Value, clock.GetUtcNow(), ct)
                is not { } sessionId)
            {
                return Failure(email, "code already used, or the authenticator was reset", OperatorProblems.MfaCodeInvalid);
            }

            audit.Write(new PlatformAuditLog
            {
                PlatformUserId = user.Id,
                Action = "operator.signed_in",
                Detail = email,
                CreatedAt = clock.GetUtcNow(),
            });

            return new SignInOutcome(sessionId, null);
        }

        private SignInOutcome Failure(
            string? email, string reason, string problemCode = OperatorProblems.InvalidCredentials)
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

            return new SignInOutcome(null, problemCode);
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
                [FromServices] KeyRing keyRing,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new SignInCommandHandler(sessions, audit, keyRing, clock);
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
