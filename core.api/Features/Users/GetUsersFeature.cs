using AppPlatform.Api;
using AppPlatform.Auth;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using AppPlatform.Outbox;
using Microsoft.AspNetCore.Mvc;

namespace AppPlatform.Core.Features.Users;

/// <summary>The Users page: every account in the tenant, with its status and roles.</summary>
public static class GetUsersFeature
{
    /// <param name="Roles">Role keys; the page names them from <c>GET /roles</c>.</param>
    /// <param name="Invitation">For an account not yet accepted: how its latest invitation is doing. Null otherwise.</param>
    public record UserModel(string UserId, string Email, string Status, IReadOnlyList<string> Roles, InvitationModel? Invitation);

    /// <summary>
    /// What an administrator needs to decide whether to resend (CLAUDE.md, "delivery status is
    /// visible on the record"). Describes the most recent invitation only; older links still work
    /// until they expire, but the newest is the one the person was last told about.
    /// </summary>
    /// <param name="Delivery">
    /// <c>pending</c> (queued or retrying), <c>delivered</c>, <c>failed</c> (the outbox gave up), or
    /// <c>unknown</c> (the outbox has pruned the email's record, or the link predates recording it).
    /// </param>
    /// <param name="Expired">True when the newest link can no longer be used.</param>
    public record InvitationModel(string Delivery, DateTimeOffset ExpiresAt, bool Expired);

    public class GetUsersQueryHandler(IUserService users, TimeProvider clock)
    {
        public async Task<CommandResult> Handle(Caller caller, CancellationToken ct = default)
        {
            var rows = await users.ListAsync(ct);

            var invited = rows.Where(u => u.Status == UserStatus.Invited).ToList();
            var tokens = invited.Count == 0 ? [] : await users.ListInvitationTokensAsync(invited.Select(u => u.Id).ToList(), ct);
            var newestByUser = tokens.GroupBy(t => t.UserId).ToDictionary(g => g.Key, g => g.MaxBy(t => t.CreatedAt)!);

            // The status of exactly the email that carried each newest link (UserToken.MessageId).
            var messageIds = newestByUser.Values.Select(t => t.MessageId).OfType<Guid>().ToList();
            var emails = messageIds.Count == 0 ? [] : await users.ListInvitationEmailsAsync(messageIds, ct);
            var statusByMessage = emails.ToDictionary(e => e.MessageId, e => e.Status);

            var now = clock.GetUtcNow();
            InvitationModel? InvitationOf(User user)
            {
                if (user.Status != UserStatus.Invited || !newestByUser.TryGetValue(user.Id, out var newest))
                    return null;

                // Unknown when the token predates the link to its email, or the outbox has pruned it.
                OutboxStatus? status = newest.MessageId is { } id && statusByMessage.TryGetValue(id, out var s) ? s : null;
                var delivery = status switch
                {
                    OutboxStatus.Pending => "pending",
                    OutboxStatus.Succeeded => "delivered",
                    OutboxStatus.Dead => "failed",
                    _ => "unknown",
                };

                return new InvitationModel(delivery, newest.ExpiresAt, newest.ExpiresAt <= now);
            }

            return CommandResult.Ok(rows.Select(u => new UserModel(
                u.PublicId, u.Email, u.Status.ToString(), u.Roles, InvitationOf(u))).ToList());
        }
    }

    public class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder routes) => routes.MapGet("/api/core/v1/users",
            async (
                [FromServices] ICallerContext callerContext,
                [FromServices] IUserService users,
                [FromServices] TimeProvider clock,
                CancellationToken ct) =>
            {
                var handler = new GetUsersQueryHandler(users, clock);
                return (await handler.Handle(callerContext.Require(), ct)).CreateIResult();
            })
            .RequireAuthorization()
            .RequirePermission(Permissions.Core.UserRead);
    }
}
