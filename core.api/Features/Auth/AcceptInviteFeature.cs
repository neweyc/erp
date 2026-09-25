using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Auth;

/// <summary>
/// Turns an invitation into a usable account by setting a password.
///
/// Anonymous by necessity — the person has no session yet — so the token IS the authentication,
/// and the tenant is taken from the token rather than from anything the caller supplies.
/// </summary>
public static class AcceptInviteFeature
{
    public record AcceptInviteCommand(string? Token, string? Password);

    public class AcceptInviteCommandHandler(
        IAuthService auth, IBackgroundTenantScope tenantScope, TimeProvider clock)
    {
        /// <summary>
        /// Short, because a password is what people choose badly. Length is the only requirement
        /// that reliably helps; composition rules push people toward Password1! and a sticky note.
        /// </summary>
        public const int MinimumPasswordLength = 12;

        public async Task<CommandResult> Handle(AcceptInviteCommand cmd, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(cmd.Token))
                return CommandResult.Invalid(AuthProblems.InvalidToken, "This invitation link is not valid.");

            if (cmd.Password is not { Length: >= MinimumPasswordLength })
            {
                return CommandResult.Invalid(
                    AuthProblems.WeakPassword,
                    $"Choose a password of at least {MinimumPasswordLength} characters.");
            }

            var token = await auth.FindTokenAsync(TokenGenerator.HashOf(cmd.Token), TokenPurpose.Invite, ct);

            // One message for not-found, used, and expired. Distinguishing them tells someone
            // holding a guessed token which half of the guess was right.
            if (token is null || token.UsedAt is not null || token.ExpiresAt <= clock.GetUtcNow())
                return CommandResult.Invalid(AuthProblems.InvalidToken, "This invitation link is not valid.");

            // Entered from the TOKEN, so everything below is filtered and stamped to the right
            // tenant without the caller having said which one they are.
            tenantScope.UseTenant(token.TenantId);

            // By the token's user id, not by an address the caller supplied: the token is the
            // only thing here that has been authenticated.
            var user = await auth.FindUserAsync(token.UserId, ct);
            if (user is null)
                return CommandResult.Invalid(AuthProblems.InvalidToken, "This invitation link is not valid.");

            user.PasswordHash = PasswordHasher.Hash(cmd.Password);
            user.Status = UserStatus.Active;

            // Consumed in the same save as the activation. EF puts used_at in the UPDATE
            // predicate, so two simultaneous uses of one link cannot both succeed.
            token.UsedAt = clock.GetUtcNow();

            await auth.SaveAsync(ct);

            return CommandResult.Ok(new { email = user.Email });
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapPost(
            "/api/core/v1/auth/accept-invite",
            async (
                AcceptInviteCommand cmd,
                [FromServices] IAuthService auth,
                [FromServices] IBackgroundTenantScope tenantScope,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new AcceptInviteCommandHandler(auth, tenantScope, clock);
                return (await handler.Handle(cmd, ct)).CreateIResult();
            })
            // Anonymous by necessity: the person has no session yet, and the token is what
            // authenticates them. Reviewed as carefully as any anonymous endpoint.
            .AllowAnonymous();
    }
}

public static class AuthProblems
{
    public const string InvalidToken = "invalid_token";
    public const string WeakPassword = "weak_password";
    public const string InvalidCredentials = "invalid_credentials";
    public const string NotSignedIn = "not_signed_in";
}
